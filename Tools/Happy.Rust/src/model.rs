use crate::{
    catalog,
    wire::{BASE, Packet, Reader, Result},
};
use serde_json::{Value, json};
use std::{
    collections::{BTreeMap, VecDeque},
    time::Instant,
};

#[derive(Default, Clone)]
pub struct Service {
    pub id: u32,
    pub name: String,
    pub machine: u8,
    pub status: u32,
    pub sessions: u32,
    pub users: u32,
    pub max_users: u32,
    pub ping: u32,
    pub peak_unix: i64,
    pub stops: u32,
    pub last_stop_unix: i64,
    pub active: u32,
    pub updated: Option<Instant>,
    pub history: VecDeque<u32>,
}
impl Service {
    pub fn status_name(&self) -> &'static str {
        match self.status {
            1 => "Stopped",
            2 => "Starting",
            3 => "Stopping",
            4 => "Running",
            5 => "Continuing",
            6 => "Pausing",
            7 => "Paused",
            _ => "Monitor only",
        }
    }
}
pub struct Platform {
    pub cpu: u32,
    pub memory: u32,
    pub network: f32,
    pub updated: Instant,
}
#[derive(Default)]
pub struct Model {
    pub authority: u8,
    pub manager: u32,
    pub read_only: bool,
    pub auto_start: bool,
    pub groups: BTreeMap<u8, String>,
    pub machines: BTreeMap<u8, String>,
    pub types: BTreeMap<u8, String>,
    pub services: BTreeMap<u32, Service>,
    pub platforms: BTreeMap<u8, Platform>,
    pub results: VecDeque<(String, Value)>,
    pub log: VecDeque<String>,
}
impl Model {
    pub fn log(&mut self, message: impl Into<String>) {
        if self.log.len() == 300 {
            self.log.pop_front();
        }
        self.log.push_back(message.into());
    }
    pub fn result(&mut self, title: String, value: Value) {
        if self.results.len() == 30 {
            self.results.pop_front();
        }
        self.results.push_back((title, value));
    }
    pub fn apply(&mut self, p: &Packet) -> Result<()> {
        let offset = p.id.checked_sub(BASE).ok_or("Unknown message plane")?;
        let mut r = Reader::new(&p.body);
        match offset {
            2 | 0x31 => {
                let ret = r.u8()?;
                let authority = r.u8()?;
                let manager = if offset == 2 { r.u32()? } else { 0 };
                r.finish()?;
                if ret != 0 || !(1..=7).contains(&authority) {
                    return Err("Operator login was denied".into());
                }
                self.authority = authority;
                self.manager = manager;
                self.read_only = offset == 0x31;
                self.log(format!(
                    "Authenticated: authority {authority}{}",
                    if self.read_only { " (read only)" } else { "" }
                ));
            }
            3..=5 => {
                let n = r.u32()? as usize;
                if n > r.remaining() / 5 {
                    return Err("Invalid topology count".into());
                }
                let mut names = BTreeMap::new();
                for _ in 0..n {
                    names.insert(r.u8()?, r.text()?);
                }
                r.finish()?;
                match offset {
                    3 => self.types = names,
                    4 => self.machines = names,
                    _ => self.groups = names,
                }
            }
            7 => {
                let n = r.u32()? as usize;
                if n > r.remaining() / 12 {
                    return Err("Invalid service count".into());
                }
                let mut services = BTreeMap::new();
                for _ in 0..n {
                    let group = r.u8()? as u32;
                    let ty = r.u8()? as u32;
                    let num = r.u8()? as u32;
                    let id = group << 16 | ty << 8 | num;
                    let name = r.text()?;
                    let machine = r.u8()?;
                    let status = r.u32()?;
                    let mut service = self.services.get(&id).cloned().unwrap_or_default();
                    service.id = id;
                    service.name = name;
                    service.machine = machine;
                    service.status = status;
                    services.insert(id, service);
                }
                r.finish()?;
                self.services = services;
            }
            0x1c => {
                let id = r.u32()?;
                let status = r.u32()?;
                r.finish()?;
                if let Some(s) = self.services.get_mut(&id) {
                    s.status = status;
                }
            }
            0x20 => {
                let id = r.u32()?;
                let sessions = r.u32()?;
                let users = r.u32()?;
                let max_users = r.u32()?;
                let ping = r.u32()?;
                let peak_unix = r.i64()?;
                let stops = r.u32()?;
                let last_stop_unix = r.i64()?;
                let active = r.u32()?;
                r.finish()?;
                if let Some(s) = self.services.get_mut(&id) {
                    s.sessions = sessions;
                    s.users = users;
                    s.max_users = max_users;
                    s.ping = ping;
                    s.peak_unix = peak_unix;
                    s.stops = stops;
                    s.last_stop_unix = last_stop_unix;
                    s.active = active;
                    s.updated = Some(Instant::now());
                    if s.history.len() == 120 {
                        s.history.pop_front();
                    }
                    s.history.push_back(users);
                }
            }
            0x35 => {
                let id = r.u8()?;
                let cpu = r.u32()?;
                let memory = r.u32()?;
                let network = r.f32()?;
                r.finish()?;
                self.platforms.insert(
                    id,
                    Platform {
                        cpu,
                        memory,
                        network,
                        updated: Instant::now(),
                    },
                );
            }
            0x4b => {
                let enabled = r.u8()?;
                r.finish()?;
                self.auto_start = enabled != 0;
            }
            0x2f => {
                r.finish()?;
                self.log("Control denied this operation (authority or read-only policy).");
            }
            0x24 => {
                r.finish()?;
            }
            0x72 => self.result(
                "Tournament response".into(),
                catalog::tournament_response(&p.body)?,
            ),
            _ => {
                let title = response_title(offset);
                if let Some(fields) = catalog::response_schema(offset) {
                    let value = r.fields(&fields)?;
                    r.finish()?;
                    self.result(title.into(), value);
                } else {
                    self.result(format!("Unrecognized response 0x{:04X}",p.id),json!({"bytes":p.body.len(),"hex":p.body.iter().map(|b|format!("{b:02X}")).collect::<Vec<_>>().join(" ")}));
                }
            }
        }
        Ok(())
    }
}
pub fn response_title(offset: u16) -> &'static str {
    match offset {
        9 => "Process control (verify service status)",
        0x22 => "Upload started",
        0x26 => "Upload finished",
        0x37 => "Monster spawns",
        0x3b => "Account block result",
        0x4d => "Chat restriction result (1 = applied)",
        0x51 => "Items",
        0x53 => "Item states",
        0x55 => "Chat restriction log",
        0x5a => "Castles",
        0x5c => "Castle guild result",
        0x62 => "Scheduled event result (0 = success)",
        0x64 => "Scheduled events",
        0x6c => "Cash items",
        0x6e => "Lucky event result (0 = success)",
        0x70 => "Lucky events",
        0x75 => "Rock paper scissors",
        0x78 => "Preversion files",
        0x7e => "Gift result",
        0x80 => "Gift catalog",
        _ => "Server response",
    }
}
