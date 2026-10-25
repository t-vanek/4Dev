use happy::{
    catalog,
    model::Model,
    wire::{self, BASE, Framer, Packet, Reader},
};
use serde_json::json;

#[test]
fn golden_chat_ban_is_byte_compatible_with_control() {
    let commands = catalog::commands();
    let c = commands.iter().find(|c| c.key == "chatban").unwrap();
    let bytes = c
        .packet(&json!({"name":"GM","minutes":60,"reason":"spam"}), 0)
        .unwrap()
        .bytes()
        .unwrap();
    assert_eq!(
        bytes,
        vec![
            32, 0, 0x4d, 0x93, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, b'G', b'M', 60, 0,
            4, 0, 0, 0, b's', b'p', b'a', b'm'
        ]
    );
}
#[test]
fn fragmented_and_coalesced_tcp_packets_are_preserved() {
    let first = Packet::new(2, vec![0, 1, 42, 0, 0, 0]);
    let second = Packet::new(0x2f, vec![]);
    let all = [first.bytes().unwrap(), second.bytes().unwrap()].concat();
    for split in 0..all.len() {
        let mut f = Framer::default();
        f.push(&all[..split]);
        let mut packets = Vec::new();
        while let Some(p) = f.packet().unwrap() {
            packets.push(p);
        }
        f.push(&all[split..]);
        while let Some(p) = f.packet().unwrap() {
            packets.push(p);
        }
        assert_eq!(packets, vec![first.clone(), second.clone()]);
    }
    let mut f = Framer::default();
    f.push(&[15, 0]);
    assert!(f.packet().is_err());
}
#[test]
fn rejects_truncation_overflow_unknown_fields_and_lossy_strings() {
    let commands = catalog::commands();
    let c = commands.iter().find(|c| c.key == "chatban").unwrap();
    for v in [
        json!({"name":"x","minutes":65536,"reason":"x"}),
        json!({"name":"Žluťoučký","minutes":1,"reason":"x"}),
        json!({"name":"x","minutes":1}),
        json!({"name":"x","minutes":1,"reason":"x","typo":1}),
    ] {
        assert!(c.packet(&v, 1).is_err());
    }
    let fields = vec![wire::field("text", wire::Kind::Text)];
    assert!(Reader::new(&[255, 255, 255, 127]).fields(&fields).is_err());
    assert!(Packet::new(1, vec![0; 65520]).bytes().is_err());
    let mut model = Model::default();
    assert!(
        model
            .apply(&Packet::new(7, vec![255, 255, 255, 255]))
            .is_err()
    );
    assert!(model.services.is_empty());
}
#[test]
fn permissions_match_the_server_including_read_only_sessions() {
    for c in catalog::commands() {
        assert!(!catalog::allowed(0, false, &c));
        assert!(!catalog::allowed(8, false, &c));
        for auth in 1..=7 {
            assert_eq!(catalog::allowed(auth, false, &c), auth <= c.authority);
            assert_eq!(catalog::allowed(auth, true, &c), c.key == "status");
        }
    }
}
#[test]
fn session_id_is_inserted_and_current_account_and_gift_layouts_are_used() {
    let commands = catalog::commands();
    let c = commands.iter().find(|c| c.key == "luckylist").unwrap();
    assert_eq!(
        c.packet(&json!({"group":1,"manager_id":999,"day":3}), 42)
            .unwrap()
            .body,
        vec![1, 42, 0, 0, 0, 3]
    );
    let c = commands.iter().find(|c| c.key == "protect").unwrap();
    let p = c
        .packet(
            &json!({"account":"A","duration":123,"reason":"B","permanent":1}),
            1,
        )
        .unwrap();
    assert_eq!(
        p.body,
        vec![1, 0, 0, 0, b'A', 123, 0, 0, 0, 1, 0, 0, 0, b'B', 1]
    );
    let c = commands.iter().find(|c| c.key == "giftadd").unwrap();
    let mut values = c.defaults();
    values["gifts"] = json!([{"gift_id":0,"gift_type":1,"value":500,"count":2,"take_type":3,"max_take_count":4,"tool_only":1,"error_gift_id":6,"title":"T","message":"M"}]);
    assert_eq!(
        c.packet(&values, 42).unwrap().body,
        vec![
            1, 1, 0, 2, 0, 0, 1, 244, 1, 0, 0, 2, 3, 4, 1, 6, 0, 1, 0, 0, 0, b'T', 1, 0, 0, 0, b'M'
        ]
    );
    let mut model = Model::default();
    model
        .apply(&Packet::new(
            0x80,
            vec![
                1, 0, 7, 0, 1, 244, 1, 0, 0, 2, 3, 4, 1, 6, 0, 1, 0, 0, 0, b'T', 1, 0, 0, 0, b'M',
            ],
        ))
        .unwrap();
    assert_eq!(model.results.back().unwrap().1["gifts"][0]["tool_only"], 1);
}
#[test]
fn monitor_layout_and_nonempty_tournament_blocks_match_current_world() {
    let mut m = Model::default();
    let mut body = vec![1, 0, 0, 0, 1, 3, 1, 1, 0, 0, 0, b'W', 1];
    body.extend(4u32.to_le_bytes());
    m.apply(&Packet::new(7, body)).unwrap();
    let mut data = vec![];
    for v in [66305u32, 5, 6, 7, 8] {
        data.extend(v.to_le_bytes());
    }
    data.extend(9i64.to_le_bytes());
    data.extend(10u32.to_le_bytes());
    data.extend(11i64.to_le_bytes());
    data.extend(12u32.to_le_bytes());
    m.apply(&Packet::new(0x20, data)).unwrap();
    assert_eq!(m.services[&66305].active, 12);
    assert_eq!(m.services[&66305].last_stop_unix, 11);
    // manager, LIST, zero schedules, one tournament, id=9, zero entries
    let value = catalog::tournament_response(&[42, 0, 0, 0, 1, 0, 1, 9, 0, 0]).unwrap();
    assert_eq!(value["data"]["tournaments"][0]["id"], 9);
    assert_eq!(
        catalog::tournament_response(&[42, 0, 0, 0, 2, 9, 0]).unwrap()["data"]["id"],
        9
    );
    assert_eq!(BASE + 0x20, 0x9321);
}
