//! Run explicitly via test.ps1: these tests use the production .NET dispatcher and file deployment.
use happy::{
    catalog,
    model::Model,
    net::{Connection, Event, Instruction},
    wire::{BASE, Packet, Reader},
};
use serde_json::json;
use std::{
    fs,
    io::{BufRead, BufReader, Write},
    path::PathBuf,
    process::{Child, Command, Stdio},
    time::{Duration, Instant},
};

struct Host {
    child: Child,
    root: PathBuf,
    port: u16,
}
impl Host {
    fn new() -> Self {
        let dll = std::env::var("HAPPY_INTEROP_DLL")
            .expect("Run test.ps1 to build and locate the .NET fixture");
        let root = std::env::current_dir()
            .unwrap()
            .join("target")
            .join(format!(
                "interop-{}-{}",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            ));
        let mut child = Command::new("dotnet")
            .arg(dll)
            .arg(&root)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .spawn()
            .unwrap();
        let mut port = String::new();
        BufReader::new(child.stdout.take().unwrap())
            .read_line(&mut port)
            .unwrap();
        Self {
            child,
            root,
            port: port.trim().parse().unwrap(),
        }
    }
    fn connect(&self, user: &str, read_only: bool) -> Connection {
        Connection::connect(
            "127.0.0.1".into(),
            self.port,
            user.into(),
            "interop-only".into(),
            read_only,
        )
    }
}
impl Drop for Host {
    fn drop(&mut self) {
        if let Some(mut input) = self.child.stdin.take() {
            let _ = writeln!(input);
        }
        let _ = self.child.wait();
        let _ = fs::remove_dir_all(&self.root);
    }
}
fn wait(connection: &Connection, mut check: impl FnMut(Event) -> bool) {
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        let e = connection
            .events
            .recv_timeout(deadline.saturating_duration_since(Instant::now()))
            .expect("Timed out waiting for fixture");
        if let Event::Disconnected(e) = &e {
            panic!("Unexpected disconnect: {e}");
        }
        if check(e) {
            return;
        }
    }
}
fn packet(connection: &Connection, offset: u16) -> Packet {
    let mut found = None;
    wait(connection, |e| {
        if let Event::Packet(p) = e
            && p.id == BASE + offset
        {
            found = Some(p);
            return true;
        }
        false
    });
    found.unwrap()
}

#[test]
#[ignore = "Requires the test-only .NET fixture; run test.ps1"]
fn real_control_login_topology_permissions_events_and_atomic_upload() {
    let host = Host::new();
    let connection = host.connect("root", false);
    let login = packet(&connection, 2);
    let mut m = Model::default();
    m.apply(&login).unwrap();
    assert_eq!(m.manager, 1);
    let status = packet(&connection, 7);
    m.apply(&status).unwrap();
    assert_eq!(m.services[&66305].name, "Fixture World");
    let golden =
        Packet::read(&mut fs::File::open(host.root.join("event-packet.bin")).unwrap()).unwrap();
    m.apply(&golden).unwrap();
    let value = m.results.back().unwrap().1["events"][0].clone();
    let commands = catalog::commands();
    let event = commands.iter().find(|c| c.key == "event").unwrap();
    let encoded = event
        .packet(&json!({"operation":2,"event":value}), m.manager)
        .unwrap();
    assert_eq!(&encoded.body[1..], &golden.body[2..]);
    connection
        .send(Instruction::Send {
            key: "event".into(),
            values: json!({"operation":2,"event":m.results.back().unwrap().1["events"][0]}),
        })
        .unwrap();
    let reply = packet(&connection, 0x62);
    assert_eq!(Reader::new(&reply.body).u8().unwrap(), 0);
    connection
        .send(Instruction::Send {
            key: "events".into(),
            values: json!({}),
        })
        .unwrap();
    assert_eq!(packet(&connection, 0x64), golden);
    let source = host.root.join("source.bin");
    let bytes: Vec<_> = (0..160_123).map(|i| (i % 251) as u8).collect();
    fs::write(&source, &bytes).unwrap();
    connection
        .send(Instruction::Upload {
            source: source.clone(),
            machine: 1,
            destination: "world/file.bin".into(),
        })
        .unwrap();
    wait(
        &connection,
        |e| matches!(e,Event::Upload{finished:true,ref message,..} if message=="File committed on Control"),
    );
    assert_eq!(
        fs::read(host.root.join("deploy/world/file.bin")).unwrap(),
        bytes
    );
    // An empty file still sends END with an explicit zero-sized final chunk.
    fs::write(&source, []).unwrap();
    connection
        .send(Instruction::Upload {
            source,
            machine: 1,
            destination: "empty.bin".into(),
        })
        .unwrap();
    wait(
        &connection,
        |e| matches!(e,Event::Upload{finished:true,ref message,..} if message=="File committed on Control"),
    );
    assert_eq!(
        fs::metadata(host.root.join("deploy/empty.bin"))
            .unwrap()
            .len(),
        0
    );
    connection.disconnect();
}
#[test]
#[ignore = "Requires the test-only .NET fixture; run test.ps1"]
fn real_status_session_gets_topology_and_cannot_mutate() {
    let host = Host::new();
    let connection = host.connect("root", true);
    let mut model = Model::default();
    model.apply(&packet(&connection, 0x31)).unwrap();
    model.apply(&packet(&connection, 5)).unwrap();
    model.apply(&packet(&connection, 4)).unwrap();
    model.apply(&packet(&connection, 3)).unwrap();
    assert_eq!(model.groups[&1], "Fixture world");
    assert_eq!(model.machines[&1], "Fixture machine");
    model.apply(&packet(&connection, 7)).unwrap();
    connection
        .send(Instruction::Send {
            key: "autostart".into(),
            values: json!({"enabled":1}),
        })
        .unwrap();
    wait(
        &connection,
        |e| matches!(e,Event::Notice(ref s) if s.contains("denied")),
    );
    assert!(model.read_only);
    connection.disconnect();
}
#[test]
#[ignore = "Requires the test-only .NET fixture; run test.ps1"]
fn real_low_privilege_session_is_blocked_in_worker() {
    let host = Host::new();
    let connection = host.connect("gm", false);
    packet(&connection, 2);
    connection
        .send(Instruction::Send {
            key: "patch".into(),
            values: json!({"files":[]}),
        })
        .unwrap();
    wait(
        &connection,
        |e| matches!(e,Event::Notice(ref s) if s.contains("denied")),
    );
    connection.disconnect();
}
