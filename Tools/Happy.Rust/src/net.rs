//! A single socket worker owns authentication, writes and the acknowledged upload state machine.
//! The UI never blocks on DNS, TCP, disk reads or acknowledgements, and never retries mutations.
use crate::{
    catalog,
    wire::{self, Framer, Packet, Reader, Result},
};
use serde_json::Value;
use std::{
    fs::File,
    io::{Read, Write},
    net::{Shutdown, TcpStream, ToSocketAddrs},
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, SyncSender},
    },
    thread,
    time::{Duration, Instant},
};

pub enum Instruction {
    Send {
        key: String,
        values: Value,
    },
    Upload {
        source: PathBuf,
        machine: u8,
        destination: String,
    },
    CancelUpload,
}
pub enum Event {
    Packet(Packet),
    Notice(String),
    Disconnected(String),
    Sent(String),
    Upload {
        done: u64,
        total: u64,
        message: String,
        finished: bool,
    },
}
pub struct Connection {
    pub events: Receiver<Event>,
    tx: SyncSender<Instruction>,
    stop: Arc<AtomicBool>,
}
struct Login {
    host: String,
    port: u16,
    user: String,
    password: String,
    read_only: bool,
}
impl Connection {
    pub fn connect(
        host: String,
        port: u16,
        user: String,
        password: String,
        read_only: bool,
    ) -> Self {
        let (tx, rx) = mpsc::sync_channel(64);
        let (events_tx, events) = mpsc::sync_channel(256);
        let stop = Arc::new(AtomicBool::new(false));
        let cancel = stop.clone();
        thread::spawn(move || {
            let result = run(
                Login {
                    host,
                    port,
                    user,
                    password,
                    read_only,
                },
                &rx,
                &events_tx,
                &cancel,
            );
            let _ = events_tx.send(Event::Disconnected(
                result.err().unwrap_or_else(|| "Disconnected".into()),
            ));
        });
        Self { events, tx, stop }
    }
    pub fn send(&self, instruction: Instruction) -> Result<()> {
        self.tx
            .try_send(instruction)
            .map_err(|_| "Connection is busy or closed".into())
    }
    pub fn disconnect(&self) {
        self.stop.store(true, Ordering::Relaxed);
    }
}
impl Drop for Connection {
    fn drop(&mut self) {
        self.disconnect();
    }
}
fn emit(tx: &SyncSender<Event>, event: Event) -> Result<()> {
    tx.send(event).map_err(|_| "Window closed".into())
}
fn send(stream: &mut TcpStream, packet: Packet) -> Result<()> {
    stream
        .write_all(&packet.bytes()?)
        .map_err(|e| e.to_string())
}
fn login(user: &str, password: &str, read_only: bool) -> Result<Packet> {
    if user.is_empty()
        || password.is_empty()
        || user.chars().count() > 50
        || password.chars().count() > 50
    {
        return Err("Operator and password must contain 1-50 Latin-1 characters".into());
    }
    let fields = vec![
        wire::field("user", wire::Kind::Text),
        wire::field("password", wire::Kind::Text),
    ];
    Ok(Packet::new(
        if read_only { 0x30 } else { 1 },
        wire::encode(
            &fields,
            &serde_json::json!({"user":user,"password":password}),
            0,
        )?,
    ))
}
struct Upload {
    file: File,
    total: u64,
    done: u64,
    cancel: bool,
    finishing: bool,
    expected: u16,
    deadline: Instant,
}
impl Upload {
    fn next(&mut self, stream: &mut TcpStream) -> Result<()> {
        if self.cancel {
            send(stream, Packet::new(0x25, vec![1]))?;
            return Ok(());
        }
        let left = self.total - self.done;
        let n = left.min(48 * 1024) as usize;
        let mut bytes = vec![0; n];
        self.file
            .read_exact(&mut bytes)
            .map_err(|e| format!("Source file changed or cannot be read: {e}"))?;
        let final_chunk = left <= 48 * 1024;
        let mut body = Vec::new();
        if final_chunk {
            body.push(0);
        }
        body.extend((n as u16).to_le_bytes());
        body.extend(bytes);
        send(
            stream,
            Packet::new(if final_chunk { 0x25 } else { 0x23 }, body),
        )?;
        self.done += n as u64;
        self.finishing = final_chunk;
        self.expected = if final_chunk { 0x26 } else { 0x24 };
        self.deadline = Instant::now() + Duration::from_secs(30);
        Ok(())
    }
}
pub fn valid_destination(name: &str) -> bool {
    !name.is_empty()
        && !name.contains(['\\', ':', '\0'])
        && name.split('/').all(|part| {
            !part.is_empty() && part != "." && part != ".." && !part.ends_with([' ', '.'])
        })
}
fn run(
    credentials: Login,
    rx: &Receiver<Instruction>,
    tx: &SyncSender<Event>,
    stop: &AtomicBool,
) -> Result<()> {
    let Login {
        host,
        port,
        user,
        password,
        read_only,
    } = credentials;
    let login = login(&user, &password, read_only)?;
    drop(password);
    emit(tx, Event::Notice(format!("Connecting to {host}:{port}")))?;
    let addresses = (host.as_str(), port)
        .to_socket_addrs()
        .map_err(|e| e.to_string())?;
    let mut connection = None;
    let mut last_error = "No address resolved".to_owned();
    for address in addresses.take(4) {
        if stop.load(Ordering::Relaxed) {
            return Ok(());
        }
        match TcpStream::connect_timeout(&address, Duration::from_secs(3)) {
            Ok(s) => {
                connection = Some(s);
                break;
            }
            Err(e) => last_error = e.to_string(),
        }
    }
    let mut stream =
        connection.ok_or_else(|| format!("Cannot connect to Control: {last_error}"))?;
    stream
        .set_read_timeout(Some(Duration::from_millis(50)))
        .map_err(|e| e.to_string())?;
    stream
        .set_write_timeout(Some(Duration::from_secs(3)))
        .map_err(|e| e.to_string())?;
    let _ = stream.set_nodelay(true);
    send(&mut stream, login)?;
    let mut permissions = None;
    let login_deadline = Instant::now() + Duration::from_secs(10);
    let mut upload: Option<Upload> = None;
    let mut framer = Framer::default();
    let mut buffer = [0; 8192];
    let commands = catalog::commands();
    while !stop.load(Ordering::Relaxed) {
        if permissions.is_none() && Instant::now() > login_deadline {
            return Err("Operator login timed out".into());
        }
        if upload.as_ref().is_some_and(|u| Instant::now() > u.deadline) {
            return Err(
                "Upload acknowledgement timed out; verify the destination before retrying".into(),
            );
        }
        for instruction in rx.try_iter().take(16) {
            let Some((authority, manager)) = permissions else {
                emit(tx, Event::Notice("Wait for operator authentication".into()))?;
                continue;
            };
            match instruction {
                Instruction::Send { key, values } => {
                    let Some(c) = commands.iter().find(|c| c.key == key) else {
                        continue;
                    };
                    if !catalog::allowed(authority, read_only, c) {
                        emit(
                            tx,
                            Event::Notice("Operation denied by session permissions".into()),
                        )?;
                        continue;
                    }
                    match c.packet(&values, manager) {
                        Ok(p) => {
                            send(&mut stream, p)?;
                            emit(tx, Event::Sent(c.title.into()))?;
                        }
                        Err(e) => emit(tx, Event::Notice(e))?,
                    }
                }
                Instruction::Upload {
                    source,
                    machine,
                    destination,
                } => {
                    if read_only || authority > 2 || upload.is_some() {
                        emit(tx,Event::Notice("Upload is unavailable for this session or another upload is active".into()))?;
                        continue;
                    }
                    if !valid_destination(&destination) {
                        emit(tx,Event::Notice("Destination must be a relative path with / separators and no traversal".into()))?;
                        continue;
                    }
                    let file = match File::open(source) {
                        Ok(file) => file,
                        Err(e) => {
                            emit(tx, Event::Notice(e.to_string()))?;
                            continue;
                        }
                    };
                    let meta = file.metadata().map_err(|e| e.to_string())?;
                    let total = meta.len();
                    if !meta.is_file() || total > 512 * 1024 * 1024 {
                        emit(
                            tx,
                            Event::Notice("Choose a regular file of at most 512 MiB".into()),
                        )?;
                        continue;
                    }
                    let fields = vec![
                        wire::field("machine", wire::Kind::U8),
                        wire::field("name", wire::Kind::Text),
                    ];
                    let body = match wire::encode(
                        &fields,
                        &serde_json::json!({"machine":machine,"name":destination}),
                        0,
                    ) {
                        Ok(body) => body,
                        Err(e) => {
                            emit(tx, Event::Notice(e))?;
                            continue;
                        }
                    };
                    send(&mut stream, Packet::new(0x21, body))?;
                    upload = Some(Upload {
                        file,
                        total,
                        done: 0,
                        cancel: false,
                        finishing: false,
                        expected: 0x22,
                        deadline: Instant::now() + Duration::from_secs(30),
                    });
                    emit(
                        tx,
                        Event::Upload {
                            done: 0,
                            total,
                            message: "Waiting for upload start".into(),
                            finished: false,
                        },
                    )?;
                }
                Instruction::CancelUpload => {
                    if let Some(u) = upload.as_mut()
                        && !u.finishing
                    {
                        u.cancel = true;
                    }
                }
            }
        }
        match stream.read(&mut buffer) {
            Ok(0) => return Err("Control closed the connection".into()),
            Ok(n) => framer.push(&buffer[..n]),
            Err(e)
                if matches!(
                    e.kind(),
                    std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                ) => {}
            Err(e) => return Err(e.to_string()),
        }
        while let Some(packet) = framer.packet()? {
            let offset = packet
                .id
                .checked_sub(wire::BASE)
                .ok_or("Unexpected message plane")?;
            if offset == 2 || offset == 0x31 {
                if permissions.is_some() || offset != if read_only { 0x31 } else { 2 } {
                    return Err("Unexpected login response".into());
                }
                let mut r = Reader::new(&packet.body);
                let ret = r.u8()?;
                let authority = r.u8()?;
                let manager = if read_only { 0 } else { r.u32()? };
                r.finish()?;
                if ret != 0 || !(1..=7).contains(&authority) || (!read_only && manager == 0) {
                    return Err("Operator login was denied".into());
                }
                permissions = Some((authority, manager));
                send(&mut stream, Packet::new(6, vec![]))?;
            } else if permissions.is_none() {
                return Err("Control sent data before authentication".into());
            }
            if matches!(offset, 0x22 | 0x24 | 0x26) && upload.is_some() {
                let u = upload.as_mut().unwrap();
                if offset != u.expected && offset != 0x26 {
                    return Err("Out-of-order upload acknowledgement".into());
                }
                if offset == 0x26 {
                    let mut r = Reader::new(&packet.body);
                    let ret = r.u8()?;
                    r.finish()?;
                    if ret == 0 && !u.finishing {
                        return Err("Upload ended before all data was sent".into());
                    }
                    emit(
                        tx,
                        Event::Upload {
                            done: u.done,
                            total: u.total,
                            message: if ret == 0 && u.finishing {
                                "File committed on Control".into()
                            } else {
                                format!("Upload failed (code {ret})")
                            },
                            finished: true,
                        },
                    )?;
                    upload = None;
                } else {
                    let mut r = Reader::new(&packet.body);
                    if offset == 0x22 && r.u8()? != 0 {
                        return Err("Upload start failed".into());
                    }
                    r.finish()?;
                    if u.finishing {
                        return Err("Unexpected upload acknowledgement".into());
                    }
                    u.next(&mut stream)?;
                    if u.cancel {
                        emit(
                            tx,
                            Event::Upload {
                                done: u.done,
                                total: u.total,
                                message: "Upload cancelled; staging discarded".into(),
                                finished: true,
                            },
                        )?;
                        upload = None;
                    } else {
                        emit(
                            tx,
                            Event::Upload {
                                done: u.done,
                                total: u.total,
                                message: if u.finishing {
                                    "Awaiting atomic commit".into()
                                } else {
                                    "Uploading".into()
                                },
                                finished: false,
                            },
                        )?;
                    }
                }
            }
            emit(tx, Event::Packet(packet))?;
        }
    }
    let _ = stream.shutdown(Shutdown::Both);
    Ok(())
}
