//! Manager requests supported by the current .NET dispatcher, not the obsolete MFC menus.
use crate::wire::{self, Field, Kind, Packet, Result, defaults, field as f};
use Kind::*;
use serde_json::{Value, json};

pub struct Command {
    pub key: &'static str,
    pub title: &'static str,
    pub category: &'static str,
    pub offset: u16,
    pub authority: u8,
    pub mutation: bool,
    pub help: &'static str,
    pub fields: Vec<Field>,
}
impl Command {
    pub fn packet(&self, values: &Value, manager: u32) -> Result<Packet> {
        Ok(Packet::new(
            self.offset,
            wire::encode(&self.fields, values, manager)?,
        ))
    }
    pub fn defaults(&self) -> Value {
        defaults(&self.fields)
    }
}
pub fn allowed(authority: u8, read_only: bool, command: &Command) -> bool {
    (1..=7).contains(&authority)
        && authority <= command.authority
        && (!read_only || command.offset == 6)
}
fn list(name: &'static str, fields: Vec<Field>) -> Field {
    f(name, List(Box::new(U16), fields))
}
fn small_list(name: &'static str, fields: Vec<Field>) -> Field {
    f(name, List(Box::new(U8), fields))
}
fn group() -> Field {
    f("group", U8).with(1)
}
fn manager() -> Field {
    f("manager_id", Manager)
}
pub fn event_fields() -> Vec<Field> {
    vec![
        f("index", U32),
        f("event_type", U8).with(1),
        f("state", U8),
        f("group", U8),
        f("server_type", U8).with(4),
        f("server_id", U8),
        f("start_unix", I64),
        f("end_unix", I64),
        f("value", U16).with(100),
        f("map_id", U16).with(255),
        f("start_alarm_seconds", U32),
        f("end_alarm_seconds", U32),
        f("start_message", Text),
        f("end_message", Text),
        f("title", Text),
        f("term", U8).with(1),
        f("lottery_message", Text),
        list("cash_items", vec![f("id", U16), f("sale_percent", U8)]),
        f("monster_start_action", U8),
        f("monster_end_action", U8),
        list("spawn_ids", vec![f("id", U16)]),
        list(
            "monster_regens",
            vec![
                f("monster_id", U16),
                f("delay", U32),
                f("map_id", U16),
                f("x", F32),
                f("y", F32),
                f("z", F32),
            ],
        ),
        list(
            "lottery_rewards",
            vec![f("item_id", U16), f("count", U8), f("winners", U16)],
        ),
    ]
}
fn lucky() -> Vec<Field> {
    let mut fields = vec![f("id", U16), f("day", U8), f("hour", U8), f("minute", U8)];
    for name in [
        "item_id_1",
        "item_id_2",
        "item_id_3",
        "item_id_4",
        "item_id_5",
    ] {
        fields.push(f(name, U16));
    }
    fields.push(f("count", U8));
    for name in [
        "present",
        "announcement",
        "title",
        "message",
        "item_1",
        "item_2",
        "item_3",
        "item_4",
        "item_5",
    ] {
        fields.push(f(name, Text));
    }
    fields
}
fn rps() -> Vec<Field> {
    vec![
        f("type", U8),
        f("win_count", U8),
        f("win_percent", U8),
        f("draw_percent", U8),
        f("lose_percent", U8),
        f("win_keep", U16),
        f("win_period", U16),
    ]
}
fn gift() -> Vec<Field> {
    vec![
        f("gift_id", U16),
        f("gift_type", U8),
        f("value", U32),
        f("count", U8),
        f("take_type", U8),
        f("max_take_count", U8),
        f("tool_only", U8),
        f("error_gift_id", U16),
        f("title", Text),
        f("message", Text),
    ]
}
fn patch() -> Vec<Field> {
    vec![f("path", Text), f("name", Text), f("size", U32)]
}
fn schedule(reply: bool) -> Vec<Field> {
    let mut steps = vec![f("step", U8), f("seconds", U32)];
    if reply {
        steps.push(f("start_unix", I64));
    }
    vec![
        f("week", U8),
        f("day", U8),
        f("start_second_of_day", U32),
        small_list("steps", steps),
    ]
}
fn entry(reply: bool) -> Vec<Field> {
    let mut fields = vec![
        f("event_id", U8),
        f("name", Text),
        f("type", U8),
        f("class_mask", U32),
        f("fee", U32),
        f("refund", U32),
        f("required_item", U16),
        f("required_count", U8),
        f("min_level", U8),
        f("max_level", U8),
        small_list(
            "rewards",
            vec![
                f("chart_type", U8),
                f("item_id", U16),
                f("count", U8),
                f("class_mask", U32),
                f("shield", U8),
            ],
        ),
    ];
    if reply {
        fields.push(small_list("players", player()));
    }
    fields
}
fn player() -> Vec<Field> {
    vec![
        f("character_id", U32),
        f("name", Text),
        f("level", U8),
        f("class", U8),
        f("country", U8),
    ]
}

pub fn commands() -> Vec<Command> {
    let mut out = Vec::new();
    macro_rules! cmd {
        ($key:literal,$title:literal,$cat:literal,$id:expr,$auth:expr,$mutates:expr,$help:literal,$fields:expr) => {
            out.push(Command {
                key: $key,
                title: $title,
                category: $cat,
                offset: $id,
                authority: $auth,
                mutation: $mutates,
                help: $help,
                fields: $fields,
            });
        };
    }
    cmd!(
        "status",
        "Refresh services",
        "Services",
        6,
        7,
        false,
        "Process state and TCP monitoring are separate. Start/stop works only for processes configured on Control; Docker containers are monitored only.",
        vec![]
    );
    cmd!(
        "control",
        "Start / stop process",
        "Services",
        8,
        4,
        true,
        "start: 1 = start, 0 = graceful stop. Stopping World also suppresses restarts for its group. Refresh status to verify the outcome; legacy control ACK values are ambiguous.",
        vec![f("service_id", U32), f("start", U8).with(1)]
    );
    cmd!(
        "reconnect",
        "Reconnect monitor",
        "Services",
        0x46,
        4,
        true,
        "Closes the monitor connection; Control reconnects automatically. This does not restart the OS process.",
        vec![f("service_id", U32)]
    );
    cmd!(
        "autostart",
        "Automatic process restart",
        "Services",
        0x4a,
        4,
        true,
        "enabled: 0 or 1. Manually stopped processes remain suppressed until explicitly started.",
        vec![f("enabled", U8).with(1)]
    );
    cmd!(
        "clear",
        "Clear monitoring counters",
        "Services",
        0x4e,
        1,
        true,
        "Resets service statistics.",
        vec![]
    );
    cmd!(
        "announce",
        "Announcement",
        "Players",
        0x29,
        5,
        true,
        "group: 0 = all groups. Delivery has no manager acknowledgement.",
        vec![f("group", U32).with(1), f("message", Text)]
    );
    cmd!(
        "kick",
        "Disconnect player",
        "Players",
        0x2b,
        6,
        true,
        "Sent to all connected maps. Delivery has no manager acknowledgement.",
        vec![f("name", Text)]
    );
    cmd!(
        "move",
        "Teleport players",
        "Players",
        0x2d,
        6,
        true,
        "Coordinates are float32. Add one row per player. No manager acknowledgement.",
        vec![
            group(),
            f("channel", U8).with(1),
            f("map_id", U16),
            f("x", F32),
            f("y", F32),
            f("z", F32),
            list("players", vec![f("name", Text)])
        ]
    );
    cmd!(
        "position",
        "Move players to character",
        "Players",
        0x43,
        6,
        true,
        "Moves the listed players to target within the selected group.",
        vec![
            group(),
            f("target", Text),
            list("players", vec![f("name", Text)])
        ]
    );
    cmd!(
        "message",
        "Message character",
        "Players",
        0x3c,
        6,
        true,
        "System message to a character. No manager acknowledgement.",
        vec![f("name", Text), f("message", Text)]
    );
    cmd!(
        "protect",
        "Block account",
        "Players",
        0x3a,
        5,
        true,
        "Current Control expects account, duration, reason AND permanent flag (missing in the old Happy sender). permanent: 0 or 1.",
        vec![
            f("account", Text),
            f("duration", U32),
            f("reason", Text),
            f("permanent", U8)
        ]
    );
    cmd!(
        "chatban",
        "Restrict chat",
        "Players",
        0x4c,
        7,
        true,
        "Duration is in minutes. A zero result means the character was not found.",
        vec![
            f("name", Text),
            f("minutes", U16).with(60),
            f("reason", Text)
        ]
    );
    cmd!(
        "banlist",
        "Chat restriction log",
        "Players",
        0x54,
        1,
        false,
        "Lists Control's in-memory restriction log.",
        vec![]
    );
    cmd!(
        "bandel",
        "Delete restriction log entry",
        "Players",
        0x56,
        1,
        true,
        "id: 0 clears the entire LOG. This does not undo the player's chat restriction.",
        vec![f("id", U32)]
    );
    cmd!(
        "spawn",
        "Find monster spawn",
        "World",
        0x36,
        5,
        false,
        "Queries maps in the selected group.",
        vec![
            group(),
            f("channel", U8).with(1),
            f("map_id", U16),
            f("spawn_id", U16)
        ]
    );
    cmd!(
        "monster",
        "Monster action",
        "World",
        0x38,
        5,
        true,
        "Action and trigger identifiers follow the game charts.",
        vec![
            group(),
            f("channel", U8).with(1),
            f("map_id", U16),
            f("monster_id", U32),
            f("action", U8),
            f("trigger_id", U32),
            f("host_id", U32),
            f("rh_id", U32),
            f("rh_type", U8),
            f("spawn_id", U16)
        ]
    );
    cmd!(
        "itemfind",
        "Find item",
        "World",
        0x50,
        1,
        false,
        "Queries the selected World's item state.",
        vec![f("item_id", U16), f("name", Text), group()]
    );
    cmd!(
        "itemstate",
        "Change item state",
        "World",
        0x52,
        1,
        true,
        "Manager id is inserted from the authenticated session.",
        vec![
            group(),
            manager(),
            list("items", vec![f("item_id", U16), f("state", U8)])
        ]
    );
    cmd!(
        "castles",
        "Castle status",
        "World",
        0x59,
        1,
        false,
        "Queries a map in the selected group.",
        vec![group()]
    );
    cmd!(
        "castleguild",
        "Override castle guilds",
        "World",
        0x5b,
        1,
        true,
        "Next war time is Unix seconds.",
        vec![
            group(),
            f("castle_id", U16),
            f("defending_guild", U32),
            f("attacking_guild", U32),
            f("war_unix", I64)
        ]
    );
    cmd!(
        "siege",
        "Castle battle state",
        "World",
        0x5d,
        1,
        true,
        "Battle state is the game's battle-status enum; duration is in seconds.",
        vec![group(), f("status", U8), f("seconds", U32)]
    );
    cmd!(
        "events",
        "List scheduled events",
        "Events",
        0x63,
        3,
        false,
        "Event-list access requires authority 1-3; editing requires 1-7, matching Control's policy.",
        vec![]
    );
    cmd!(
        "event",
        "Edit scheduled event",
        "Events",
        0x61,
        7,
        true,
        "operation: 0 delete, 1 add, 2 update. Types 1-15 follow EventType in TControl.Protocol. Times are Unix seconds. term: 0 daily, 1 whole interval. group/server_id: 0 all; map_id: 255 all.",
        vec![
            f("operation", U8).with(1),
            f("event", Record(event_fields()))
        ]
    );
    cmd!(
        "cashitems",
        "Cash item catalog",
        "Events",
        0x6b,
        3,
        false,
        "Sellable item identifiers for cash-sale events.",
        vec![]
    );
    cmd!(
        "luckylist",
        "Lucky event list",
        "Events",
        0x6f,
        1,
        false,
        "day follows the World's lucky-event schedule.",
        vec![group(), manager(), f("day", U8)]
    );
    cmd!(
        "lucky",
        "Edit lucky event",
        "Events",
        0x6d,
        1,
        true,
        "operation: 0 delete, 1 add, 2 update.",
        vec![
            group(),
            manager(),
            f("operation", U8).with(1),
            f("event", Record(lucky()))
        ]
    );
    cmd!(
        "help",
        "Scheduled help message",
        "Events",
        0x73,
        1,
        true,
        "Message type follows HELPMESSAGE_TYPE; dates are Unix seconds.",
        vec![
            group(),
            f("type", U8).with(1),
            f("start_unix", I64),
            f("end_unix", I64),
            f("message", Text)
        ]
    );
    cmd!(
        "rpslist",
        "Rock paper scissors settings",
        "Events",
        0x74,
        1,
        false,
        "Reads RPS chart values.",
        vec![group()]
    );
    cmd!(
        "rps",
        "Edit rock paper scissors",
        "Events",
        0x76,
        1,
        true,
        "Probabilities are byte percentages.",
        vec![group(), list("entries", rps())]
    );
    cmd!(
        "gift",
        "Give cash mall gift",
        "Gifts",
        0x7d,
        1,
        true,
        "group: 0 sends to all connected Worlds.",
        vec![group(), f("target", Text), f("gift_id", U16)]
    );
    cmd!(
        "gifts",
        "Gift catalog",
        "Gifts",
        0x7f,
        1,
        false,
        "Uses the current .NET World layout, which omits the obsolete MFC minimum-level byte.",
        vec![group()]
    );
    for (key, title, operation) in [
        ("giftadd", "Add gifts", 2),
        ("giftedit", "Update gifts", 3),
        ("giftdel", "Delete gifts", 1),
    ] {
        let mut fields = vec![f("operation", Fixed(operation))];
        if operation == 1 {
            fields.push(f("gift_id", U16));
        } else {
            fields.extend(gift());
        }
        out.push(Command {
            key,
            title,
            category: "Gifts",
            offset: 0x81,
            authority: 1,
            mutation: true,
            help: "Edits the World's gift catalog. Each row uses the selected operation.",
            fields: vec![group(), list("gifts", fields)],
        });
    }
    cmd!(
        "preversions",
        "Preversion file catalog",
        "Patches",
        0x77,
        2,
        false,
        "Reads TPREVERSION. Files must be deployed separately.",
        vec![]
    );
    cmd!(
        "preversion",
        "Update preversion catalog",
        "Patches",
        0x79,
        2,
        true,
        "Promote/delete by beta version. New records describe files already deployed to patch hosting.",
        vec![
            list("promote", vec![f("version", U32)]),
            list("delete", vec![f("version", U32)]),
            list("new_files", patch())
        ]
    );
    cmd!(
        "patch",
        "Register patch files",
        "Patches",
        0x27,
        2,
        true,
        "Updates TVERSION metadata. Upload archives to patch hosting separately; this command does not transfer files and has no acknowledgement.",
        vec![list("files", patch())]
    );
    for (key, title, operation) in [
        ("tournaments", "Tournament list", 1),
        ("schedule", "Add tournament schedule", 2),
        ("unschedule", "Delete tournament schedule", 3),
        ("entries", "Set tournament entries", 4),
        ("entrydel", "Delete tournament entry", 5),
        ("playeradd", "Add tournament player", 6),
        ("playerdel", "Remove tournament player", 7),
        ("playerend", "Finish tournament registration", 8),
    ] {
        let mut fields = vec![group(), manager(), f("operation", Fixed(operation))];
        if (2..=7).contains(&operation) {
            fields.push(f("tournament_id", U16));
        }
        match operation {
            2 => fields.extend(schedule(false)),
            4 => fields.push(small_list("entries", entry(false))),
            5 => fields.push(f("event_id", U8)),
            6 | 7 => fields.extend([f("event_id", U8), f("name", Text)]),
            _ => {}
        }
        out.push(Command {key,title,category:"Tournaments",offset:0x71,authority:1,mutation:operation!=1,help:"Schedule times and step durations are in seconds. Manager id is filled automatically.",fields});
    }
    out
}

pub fn response_schema(offset: u16) -> Option<Vec<Field>> {
    Some(match offset {
        0x37 => vec![
            f("map_id", U16),
            f("spawn_id", U16),
            small_list(
                "monsters",
                vec![
                    f("id", U32),
                    f("host", U32),
                    f("state", U8),
                    f("x", F32),
                    f("y", F32),
                    f("z", F32),
                ],
            ),
        ],
        0x51 => vec![list(
            "items",
            vec![f("item_id", U16), f("state", U8), f("name", Text)],
        )],
        0x53 => vec![
            f("manager_id", U32),
            list("items", vec![f("item_id", U16), f("state", U8)]),
        ],
        0x55 => vec![list(
            "bans",
            vec![
                f("id", U32),
                f("name", Text),
                f("time_unix", I64),
                f("minutes", U16),
                f("reason", Text),
                f("operator", Text),
            ],
        )],
        0x5a => vec![
            f("manager_id", U32),
            list(
                "castles",
                vec![
                    f("id", U16),
                    f("name", Text),
                    f("state", U8),
                    f("defending_guild", U32),
                    f("defending_name", Text),
                    f("country", U8),
                    f("attacking_guild", U32),
                    f("attacking_name", Text),
                    f("war_unix", I64),
                ],
            ),
        ],
        0x5c => vec![
            f("manager_id", U32),
            f("result", U8),
            f("castle_id", U16),
            f("defending_guild", U32),
            f("defending_name", Text),
            f("attacking_guild", U32),
            f("attacking_name", Text),
            f("war_unix", I64),
        ],
        0x62 => vec![
            f("result", U8),
            f("operation", U8),
            f("event", Record(event_fields())),
        ],
        0x64 => vec![list("events", event_fields())],
        0x6c => vec![list("items", vec![f("id", U16), f("name", Text)])],
        0x6e => vec![
            f("result", U8),
            f("manager_id", U32),
            f("operation", U8),
            f("event", Record(lucky())),
        ],
        0x70 => vec![f("manager_id", U32), list("events", lucky())],
        0x75 => vec![f("changed", U8), group(), list("entries", rps())],
        0x78 => {
            let mut fields = vec![f("version", U32)];
            fields.extend(patch());
            vec![list("files", fields)]
        }
        0x80 => vec![list("gifts", gift())],
        0x3b | 0x4d | 0x7e | 0x09 | 0x22 | 0x26 => vec![f("result", U8)],
        _ => return None,
    })
}
pub fn tournament_response(data: &[u8]) -> Result<Value> {
    let mut r = wire::Reader::new(data);
    let manager = r.u32()?;
    let operation = r.u8()?;
    let fields = match operation {
        1 => {
            let mut sched = vec![f("id", U16)];
            sched.extend(schedule(true));
            vec![
                small_list("schedules", sched),
                small_list(
                    "tournaments",
                    vec![f("id", U16), small_list("entries", entry(true))],
                ),
            ]
        }
        2 | 3 => vec![f("id", U16)],
        4 | 5 | 8 => vec![],
        6 => {
            let mut fields = vec![f("event_id", U8)];
            fields.extend(player());
            fields
        }
        7 => vec![f("event_id", U8), f("character_id", U32)],
        _ => return Err(format!("Unknown tournament operation {operation}")),
    };
    let value = r.fields(&fields)?;
    r.finish()?;
    Ok(json!({"manager_id":manager,"operation":operation,"data":value}))
}
