use happy::{
    net::{Connection, Event, Instruction},
    wire::{BASE, Packet, Reader},
};
use std::{
    fs,
    io::Write,
    net::{TcpListener, TcpStream},
    sync::mpsc,
    thread,
    time::Duration,
};

fn send(stream: &mut TcpStream, offset: u16, body: Vec<u8>) {
    stream
        .write_all(&Packet::new(offset, body).bytes().unwrap())
        .unwrap();
}
fn receive(stream: &mut TcpStream, offset: u16) -> Packet {
    let p = Packet::read(stream).unwrap();
    assert_eq!(p.id, BASE + offset);
    p
}
fn authenticated(stream: &mut TcpStream) {
    let p = receive(stream, 1);
    let mut r = Reader::new(&p.body);
    assert_eq!(r.text().unwrap(), "operator");
    assert_eq!(r.text().unwrap(), "test-only");
    send(stream, 2, vec![0, 1, 42, 0, 0, 0]);
    receive(stream, 6);
}
fn wait(c: &Connection, mut predicate: impl FnMut(Event) -> bool) {
    for _ in 0..100 {
        let e = c.events.recv_timeout(Duration::from_secs(5)).unwrap();
        if predicate(e) {
            return;
        }
    }
    panic!("Expected event was not received");
}
#[test]
fn cancelled_upload_waits_for_inflight_ack_then_allows_a_new_upload() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    let (started_tx, started_rx) = mpsc::channel();
    let (resume_tx, resume_rx) = mpsc::channel();
    let server = thread::spawn(move || {
        let (mut s, _) = listener.accept().unwrap();
        s.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
        authenticated(&mut s);
        receive(&mut s, 0x21);
        started_tx.send(()).unwrap();
        resume_rx.recv().unwrap();
        send(&mut s, 0x22, vec![0]);
        loop {
            let p = Packet::read(&mut s).unwrap();
            if p.id == BASE + 0x25 {
                assert_eq!(p.body, vec![1]);
                break;
            }
            assert_eq!(p.id, BASE + 0x23);
            send(&mut s, 0x24, vec![]);
        }
        // Cancel has no acknowledgement in current Control. The next START follows it on the same TCP stream.
        receive(&mut s, 0x21);
        send(&mut s, 0x22, vec![0]);
        let p = receive(&mut s, 0x25);
        assert_eq!(p.body, vec![0, 0, 0]);
        send(&mut s, 0x26, vec![0]);
        let mut b = [0];
        use std::io::Read;
        let _ = s.read(&mut b);
    });
    let c = Connection::connect(
        "127.0.0.1".into(),
        port,
        "operator".into(),
        "test-only".into(),
        false,
    );
    wait(&c, |e| matches!(e,Event::Packet(ref p) if p.id==BASE+2));
    let root = std::env::current_dir()
        .unwrap()
        .join("target")
        .join(format!("network-test-{}", std::process::id()));
    fs::create_dir_all(&root).unwrap();
    let source = root.join("source.bin");
    fs::write(&source, vec![1; 200_000]).unwrap();
    c.send(Instruction::Upload {
        source: source.clone(),
        machine: 1,
        destination: "file.bin".into(),
    })
    .unwrap();
    started_rx.recv_timeout(Duration::from_secs(5)).unwrap();
    c.send(Instruction::CancelUpload).unwrap();
    resume_tx.send(()).unwrap();
    wait(
        &c,
        |e| matches!(e,Event::Upload{finished:true,ref message,..} if message.contains("cancelled")),
    );
    fs::write(&source, []).unwrap();
    c.send(Instruction::Upload {
        source,
        machine: 1,
        destination: "empty.bin".into(),
    })
    .unwrap();
    wait(
        &c,
        |e| matches!(e,Event::Upload{finished:true,ref message,..} if message.contains("committed")),
    );
    c.disconnect();
    server.join().unwrap();
    fs::remove_dir_all(root).unwrap();
}
#[test]
fn failed_authentication_and_truncated_frame_close_the_session() {
    for fail_login in [true, false] {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let server = thread::spawn(move || {
            let (mut s, _) = listener.accept().unwrap();
            receive(&mut s, 1);
            if fail_login {
                send(&mut s, 2, vec![1, 0, 0, 0, 0, 0]);
            } else {
                s.write_all(&[15, 0]).unwrap();
            }
        });
        let c = Connection::connect(
            "127.0.0.1".into(),
            port,
            "operator".into(),
            "test-only".into(),
            false,
        );
        wait(
            &c,
            |e| matches!(e,Event::Disconnected(ref s) if s.contains(if fail_login{"denied"}else{"packet length"})),
        );
        server.join().unwrap();
    }
}
