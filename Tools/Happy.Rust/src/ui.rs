use eframe::egui::{self, Color32, RichText, Ui};
use happy::{
    catalog::{self, Command},
    model::Model,
    net::{Connection, Event, Instruction},
    wire::{self, Field, Kind},
};
use serde_json::{Value, json};
use std::{
    path::PathBuf,
    time::{Duration, SystemTime, UNIX_EPOCH},
};

const ACCENT: Color32 = Color32::from_rgb(94, 221, 199);
const DIM: Color32 = Color32::from_rgb(151, 166, 188);
#[derive(PartialEq, Clone, Copy)]
enum Page {
    Monitor,
    Commands,
    Upload,
    Results,
}
enum Pending {
    Command(usize, Value),
    Upload {
        source: PathBuf,
        machine: u8,
        destination: String,
    },
}
pub struct Happy {
    model: Model,
    connection: Option<Connection>,
    connecting: bool,
    host: String,
    port: String,
    user: String,
    password: String,
    read_only: bool,
    page: Page,
    commands: Vec<Command>,
    command: usize,
    values: Value,
    category: String,
    search: String,
    selected: Option<u32>,
    pending: Option<Pending>,
    error: String,
    source: String,
    destination: String,
    machine: u8,
    upload: Option<(u64, u64, String)>,
    upload_busy: bool,
    json_editor: String,
    export_path: String,
}
impl Happy {
    pub fn new(ctx: &egui::Context) -> Self {
        ctx.set_theme(egui::Theme::Dark);
        ctx.set_visuals(egui::Visuals::dark());
        let mut style = (*ctx.style()).clone();
        style.spacing.item_spacing = egui::vec2(10.0, 8.0);
        style.spacing.button_padding = egui::vec2(12.0, 7.0);
        style.visuals.panel_fill = Color32::from_rgb(18, 25, 38);
        style.visuals.window_fill = Color32::from_rgb(24, 33, 48);
        style.visuals.selection.bg_fill = Color32::from_rgb(34, 91, 89);
        ctx.set_style(style);
        let commands = catalog::commands();
        let values = commands[0].defaults();
        Self {
            model: Model::default(),
            connection: None,
            connecting: false,
            host: std::env::var("HAPPY_HOST").unwrap_or_else(|_| "127.0.0.1".into()),
            port: std::env::var("HAPPY_PORT").unwrap_or_else(|_| "3615".into()),
            user: String::new(),
            password: String::new(),
            read_only: false,
            page: Page::Monitor,
            commands,
            command: 0,
            values,
            category: "Services".into(),
            search: String::new(),
            selected: None,
            pending: None,
            error: String::new(),
            source: String::new(),
            destination: String::new(),
            machine: 0,
            upload: None,
            upload_busy: false,
            json_editor: String::new(),
            export_path: "happy-services.csv".into(),
        }
    }
    fn process_events(&mut self) {
        let events: Vec<_> = self
            .connection
            .as_ref()
            .map(|c| c.events.try_iter().take(1000).collect())
            .unwrap_or_default();
        for event in events {
            match event {
                Event::Packet(p) => {
                    if p.id == wire::BASE + 2 || p.id == wire::BASE + 0x31 {
                        self.error.clear();
                    }
                    if let Err(e) = self.model.apply(&p) {
                        self.error = format!("Response 0x{:04X}: {e}", p.id);
                        self.model.log(self.error.clone());
                    }
                }
                Event::Notice(s) => {
                    self.error = s.clone();
                    self.model.log(s);
                }
                Event::Sent(s) => self.model.log(format!(
                    "Sent: {s}. Await server response or refresh status to verify."
                )),
                Event::Disconnected(s) => {
                    if self.upload_busy
                        && let Some((_, _, message)) = &mut self.upload
                    {
                        *message =
                            "Connection closed; verify the destination before retrying".into();
                    }
                    self.model.log(s.clone());
                    self.error = s;
                    self.model.authority = 0;
                    self.model.manager = 0;
                    self.connecting = false;
                    self.connection = None;
                    self.pending = None;
                    self.upload_busy = false;
                }
                Event::Upload {
                    done,
                    total,
                    message,
                    finished,
                } => {
                    self.upload = Some((done, total, message.clone()));
                    self.upload_busy = !finished;
                    if finished {
                        self.model.log(message);
                    }
                }
            }
        }
        if self.model.authority != 0 {
            self.connecting = false;
        }
    }
    fn send(&mut self, instruction: Instruction) {
        self.error.clear();
        match self
            .connection
            .as_ref()
            .ok_or_else(|| "Connect to Control first".to_owned())
            .and_then(|c| c.send(instruction))
        {
            Ok(()) => {}
            Err(e) => self.error = e,
        }
    }
    fn choose(&mut self, index: usize) {
        self.command = index;
        self.values = self.commands[index].defaults();
        self.json_editor.clear();
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_secs();
        if self.commands[index].key == "event" {
            self.values["event"]["start_unix"] = json!(now + 60);
            self.values["event"]["end_unix"] = json!(now + 3660);
        }
    }
    fn action(&mut self, key: &str, values: Value) {
        if let Some(index) = self.commands.iter().position(|c| c.key == key) {
            if self.commands[index].mutation {
                self.pending = Some(Pending::Command(index, values));
            } else {
                self.send(Instruction::Send {
                    key: key.into(),
                    values,
                });
            }
        }
    }
    fn connect(&mut self) {
        let port = match self.port.parse::<u16>() {
            Ok(p) if p > 0 => p,
            _ => {
                self.error = "Enter a port from 1 to 65535".into();
                return;
            }
        };
        self.error.clear();
        self.model = Model::default();
        self.selected = None;
        self.pending = None;
        self.upload = None;
        self.upload_busy = false;
        self.connection = Some(Connection::connect(
            self.host.trim().to_owned(),
            port,
            self.user.clone(),
            std::mem::take(&mut self.password),
            self.read_only,
        ));
        self.connecting = true;
    }
    fn show(&mut self, ctx: &egui::Context) {
        self.process_events();
        ctx.request_repaint_after(Duration::from_millis(100));
        egui::TopBottomPanel::top("header").show(ctx, |ui| {
            ui.add_space(8.0);
            ui.horizontal(|ui| {
                ui.label(RichText::new("HAPPY").size(25.0).strong().color(ACCENT));
                ui.label(RichText::new("4Dev operator console").color(DIM));
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    let text = if self.model.authority > 0 {
                        format!(
                            "{} · authority {}{}",
                            self.user,
                            self.model.authority,
                            if self.model.read_only {
                                " · read only"
                            } else {
                                ""
                            }
                        )
                    } else if self.connecting {
                        "Connecting…".into()
                    } else {
                        "Offline".into()
                    };
                    ui.label(RichText::new(text).color(if self.model.authority > 0 {
                        ACCENT
                    } else {
                        DIM
                    }));
                });
            });
            ui.add_space(8.0);
        });
        egui::SidePanel::left("navigation").exact_width(240.0).show(ctx,|ui|{
            ui.add_space(12.0);ui.label(RichText::new("CONTROL CONNECTION").small().color(DIM));
            ui.add_enabled_ui(self.connection.is_none(),|ui|{
                ui.label("Host");ui.text_edit_singleline(&mut self.host);
                ui.horizontal(|ui|{ui.label("Port");ui.add(egui::TextEdit::singleline(&mut self.port).desired_width(70.0));});
                ui.label("Operator");ui.text_edit_singleline(&mut self.user);
                ui.label("Password");ui.add(egui::TextEdit::singleline(&mut self.password).password(true));
                ui.checkbox(&mut self.read_only,"Read-only status session");
            });
            if self.connection.is_none() {
                if ui.add_sized([216.0,36.0],egui::Button::new("Connect")).clicked(){self.connect();}
            } else if ui.add_sized([216.0,36.0],egui::Button::new("Disconnect")).clicked(){if let Some(c)=&self.connection{c.disconnect();}self.pending=None;}
            ui.label(RichText::new("Uses the existing plaintext manager connection. Use localhost or a private tunnel for remote access.").small().color(DIM));
            ui.add_space(16.0);ui.separator();ui.add_space(8.0);
            for (page,label) in [(Page::Monitor,"Overview & services"),(Page::Commands,"Operator commands"),(Page::Upload,"Deploy file"),(Page::Results,"Responses & activity")] {
                ui.selectable_value(&mut self.page,page,label);
            }
            ui.add_space(18.0);ui.label(RichText::new("TControlSvr.Net · :3615").small().color(DIM));
            ui.label(RichText::new("Only the current server's supported operations are available.").small().color(DIM));
        });
        egui::TopBottomPanel::bottom("status").show(ctx, |ui| {
            if self.error.is_empty() {
                ui.label(
                    RichText::new(
                        self.model
                            .log
                            .back()
                            .map(String::as_str)
                            .unwrap_or("Ready. Sign in with a TOPLogin operator account."),
                    )
                    .small()
                    .color(DIM),
                );
            } else {
                ui.horizontal_wrapped(|ui| {
                    ui.colored_label(Color32::LIGHT_RED, &self.error);
                    if ui.small_button("Dismiss").clicked() {
                        self.error.clear();
                    }
                });
            }
        });
        egui::CentralPanel::default().show(ctx, |ui| {
            ui.add_space(12.0);
            match self.page {
                Page::Monitor => self.monitor(ui),
                Page::Commands => self.commands_ui(ui),
                Page::Upload => self.upload_ui(ui),
                Page::Results => self.results(ui),
            }
        });
        self.review(ctx);
    }
    fn monitor(&mut self, ui: &mut Ui) {
        ui.heading("Service overview");
        ui.label(
            RichText::new("Live user counts, host utilization and managed process state")
                .color(DIM),
        );
        ui.add_space(10.0);
        ui.horizontal(|ui| {
            let running = self
                .model
                .services
                .values()
                .filter(|s| s.status == 4)
                .count();
            ui.label(
                RichText::new(format!("{running} / {} running", self.model.services.len()))
                    .strong()
                    .color(ACCENT),
            );
            ui.separator();
            ui.label(format!(
                "{} users",
                self.model
                    .services
                    .values()
                    .map(|s| s.users as u64)
                    .sum::<u64>()
            ));
            ui.separator();
            ui.label(format!(
                "Auto restart: {}",
                if self.model.auto_start { "on" } else { "off" }
            ));
            if ui
                .add_enabled(self.model.authority > 0, egui::Button::new("Refresh"))
                .clicked()
            {
                self.action("status", json!({}));
            }
        });
        ui.add_space(10.0);
        for (id, p) in &self.model.platforms {
            ui.horizontal(|ui| {
                ui.label(
                    self.model
                        .machines
                        .get(id)
                        .cloned()
                        .unwrap_or_else(|| format!("Machine {id}")),
                );
                ui.add(
                    egui::ProgressBar::new(p.cpu.min(100) as f32 / 100.0)
                        .desired_width(140.0)
                        .text(format!("CPU {}%", p.cpu)),
                );
                ui.add(
                    egui::ProgressBar::new(p.memory.min(100) as f32 / 100.0)
                        .desired_width(140.0)
                        .text(format!("Memory {}%", p.memory)),
                );
                ui.label(format!(
                    "Network {:.1}% · {}s ago",
                    p.network,
                    p.updated.elapsed().as_secs()
                ));
            });
        }
        ui.horizontal(|ui| {
            ui.label("Filter");
            ui.add(
                egui::TextEdit::singleline(&mut self.search)
                    .desired_width(270.0)
                    .hint_text("Name, group or service id"),
            );
        });
        let filter = self.search.to_lowercase();
        egui::ScrollArea::both()
            .id_salt("services")
            .max_height(310.0)
            .show(ui, |ui| {
                egui::Grid::new("service_grid")
                    .striped(true)
                    .min_col_width(50.0)
                    .show(ui, |ui| {
                        for title in [
                            "Service",
                            "Group / type / id",
                            "Machine",
                            "Process",
                            "Users",
                            "Active",
                            "Peak",
                            "Ping",
                            "Age",
                        ] {
                            ui.label(RichText::new(title).color(DIM));
                        }
                        ui.end_row();
                        for (&id, s) in &self.model.services {
                            let group = (id >> 16) as u8;
                            let ty = (id >> 8) as u8;
                            let group_name = self
                                .model
                                .groups
                                .get(&group)
                                .cloned()
                                .unwrap_or_else(|| group.to_string());
                            if !filter.is_empty()
                                && !format!("{} {} {} {id} {id:06x}", s.name, group_name, group)
                                    .to_lowercase()
                                    .contains(&filter)
                            {
                                continue;
                            }
                            if ui
                                .selectable_label(self.selected == Some(id), &s.name)
                                .clicked()
                            {
                                self.selected = Some(id);
                            }
                            ui.label(format!("{group_name} / {ty} / {}", id as u8));
                            ui.label(
                                self.model
                                    .machines
                                    .get(&s.machine)
                                    .cloned()
                                    .unwrap_or_else(|| s.machine.to_string()),
                            );
                            ui.colored_label(
                                if s.status == 4 { ACCENT } else { DIM },
                                s.status_name(),
                            );
                            ui.label(s.users.to_string());
                            ui.label(s.active.to_string());
                            ui.label(s.max_users.to_string());
                            ui.label(if s.ping == 0 {
                                "—".into()
                            } else {
                                format!("{} ms", s.ping)
                            });
                            ui.label(
                                s.updated
                                    .map(|t| format!("{}s", t.elapsed().as_secs()))
                                    .unwrap_or_else(|| "—".into()),
                            );
                            ui.end_row();
                        }
                    });
            });
        if self.model.services.is_empty() {
            ui.add_space(25.0);
            ui.label("Connect to load the configured service topology.");
        }
        if let Some(id) = self.selected {
            ui.separator();
            if let Some(s) = self.model.services.get(&id) {
                ui.label(RichText::new(format!("{} · {id} (0x{id:06X})", s.name)).strong());
                ui.label(format!(
                    "Sessions: {} · Stops: {} · Last stop (Unix): {} · Peak (Unix): {}",
                    s.sessions, s.stops, s.last_stop_unix, s.peak_unix
                ));
                history(ui, &s.history);
                let controllable = s.status != u32::MAX
                    && self.model.authority > 0
                    && self.model.authority <= 4
                    && !self.model.read_only;
                ui.horizontal(|ui| {
                    if ui
                        .add_enabled(controllable, egui::Button::new("Start process"))
                        .clicked()
                    {
                        self.action("control", json!({"service_id":id,"start":1}));
                    }
                    if ui
                        .add_enabled(controllable, egui::Button::new("Stop process"))
                        .clicked()
                    {
                        self.action("control", json!({"service_id":id,"start":0}));
                    }
                    if ui
                        .add_enabled(
                            self.model.authority > 0
                                && self.model.authority <= 4
                                && !self.model.read_only,
                            egui::Button::new("Reconnect monitor"),
                        )
                        .clicked()
                    {
                        self.action("reconnect", json!({"service_id":id}));
                    }
                });
            }
        }
        ui.add_space(8.0);
        ui.collapsing("Export current service table", |ui| {
            ui.horizontal(|ui| {
                ui.text_edit_singleline(&mut self.export_path);
                if ui.button("Write CSV").clicked() {
                    let mut csv = "id,name,status,users,active,peak,ping_ms\n".to_owned();
                    for s in self.model.services.values() {
                        csv.push_str(&format!(
                            "{},\"{}\",{},{},{},{},{}\n",
                            s.id,
                            s.name.replace('"', "\"\""),
                            s.status,
                            s.users,
                            s.active,
                            s.max_users,
                            s.ping
                        ));
                    }
                    match std::fs::write(&self.export_path, csv) {
                        Ok(()) => self.model.log(format!("Exported {}", self.export_path)),
                        Err(e) => self.error = e.to_string(),
                    }
                }
            });
        });
    }
    fn commands_ui(&mut self, ui: &mut Ui) {
        ui.heading("Operator commands");
        ui.label(RichText::new("Commands follow the permissions returned by Control. Changes are reviewed before sending.").color(DIM));
        let previous_category = self.category.clone();
        ui.horizontal_wrapped(|ui| {
            for cat in [
                "Services",
                "Players",
                "World",
                "Events",
                "Gifts",
                "Tournaments",
                "Patches",
            ] {
                ui.selectable_value(&mut self.category, cat.into(), cat);
            }
        });
        if self.category != previous_category
            && let Some(index) = self
                .commands
                .iter()
                .position(|c| c.category == self.category)
        {
            self.choose(index);
        }
        let choices: Vec<_> = self
            .commands
            .iter()
            .enumerate()
            .filter(|(_, c)| c.category == self.category)
            .map(|(i, c)| {
                (
                    i,
                    c.title,
                    catalog::allowed(self.model.authority, self.model.read_only, c),
                )
            })
            .collect();
        egui::ComboBox::from_id_salt("commands")
            .width(360.0)
            .selected_text(self.commands[self.command].title)
            .show_ui(ui, |ui| {
                for (i, title, allowed) in choices {
                    if ui
                        .selectable_label(
                            self.command == i,
                            if allowed {
                                title.to_owned()
                            } else {
                                format!("{title} (restricted)")
                            },
                        )
                        .clicked()
                    {
                        self.choose(i);
                    }
                }
            });
        let c = &self.commands[self.command];
        let allowed = catalog::allowed(self.model.authority, self.model.read_only, c);
        ui.label(c.help);
        ui.label(
            RichText::new(format!(
                "Required authority: 1–{} · {}",
                c.authority,
                if allowed {
                    "available"
                } else {
                    "restricted in this session"
                }
            ))
            .small()
            .color(if allowed { ACCENT } else { DIM }),
        );
        let fields = c.fields.clone();
        egui::ScrollArea::vertical().id_salt("form").max_height(ui.available_height()-80.0).show(ui,|ui|{
            form(ui,&fields,&mut self.values,self.model.manager);
            ui.collapsing("JSON import / export",|ui|{
                ui.label("Useful for repeatable operations. Manager id is always taken from the session.");
                ui.horizontal(|ui| {
                    if ui.button("Export form").clicked() {
                        self.json_editor = serde_json::to_string_pretty(&self.values).unwrap_or_default();
                    }
                    if ui.button("Import JSON").clicked() {
                        match serde_json::from_str::<Value>(&self.json_editor) {
                            Ok(v) if v.is_object() => self.values = v,
                            Ok(_) => self.error = "Expected a JSON object".into(),
                            Err(e) => self.error = e.to_string(),
                        }
                    }
                });
                ui.add(egui::TextEdit::multiline(&mut self.json_editor).font(egui::TextStyle::Monospace).desired_rows(8).desired_width(f32::INFINITY));
            });
        });
        let validation = self.commands[self.command].packet(&self.values, self.model.manager);
        if let Err(e) = &validation {
            ui.colored_label(Color32::LIGHT_RED, e);
        }
        ui.horizontal(|ui| {
            if ui
                .add_enabled(
                    allowed && validation.is_ok(),
                    egui::Button::new(if self.commands[self.command].mutation {
                        "Review operation"
                    } else {
                        "Send query"
                    }),
                )
                .clicked()
            {
                let key = self.commands[self.command].key;
                self.action(key, self.values.clone());
            }
            if ui.button("Reset form").clicked() {
                self.choose(self.command);
            }
        });
    }
    fn upload_ui(&mut self, ui: &mut Ui) {
        ui.heading("Deploy file");
        ui.label(
            RichText::new("Atomic file replacement through Control's configured UploadRoot")
                .color(DIM),
        );
        ui.add_space(12.0);
        ui.label("Source file (local path; files can also be dropped into the window)");
        ui.text_edit_singleline(&mut self.source);
        ui.label("Destination relative to UploadRoot (for example: world/TWorld.Server.dll)");
        ui.text_edit_singleline(&mut self.destination);
        ui.horizontal(|ui| {
            ui.label("Control LocalMachineId");
            ui.add(egui::DragValue::new(&mut self.machine));
        });
        ui.label("Remote SCM/SMB and FTP deployment are replaced by Control's local upload directory. Deploying here does not publish patch downloads. Stop the affected process before replacing its executable.");
        let allowed =
            self.model.authority > 0 && self.model.authority <= 2 && !self.model.read_only;
        if ui
            .add_enabled(
                allowed
                    && !self.upload_busy
                    && !self.source.is_empty()
                    && happy::net::valid_destination(&self.destination),
                egui::Button::new("Review file deployment"),
            )
            .clicked()
        {
            self.pending = Some(Pending::Upload {
                source: PathBuf::from(&self.source),
                machine: self.machine,
                destination: self.destination.clone(),
            });
        }
        if let Some((done, total, message)) = &self.upload {
            ui.add_space(15.0);
            ui.add(
                egui::ProgressBar::new(if *total == 0 {
                    0.0
                } else {
                    *done as f32 / *total as f32
                })
                .text(format!("{done} / {total} bytes")),
            );
            ui.label(message);
        }
        if ui
            .add_enabled(self.upload_busy, egui::Button::new("Cancel upload"))
            .clicked()
        {
            self.send(Instruction::CancelUpload);
        }
        ui.label(RichText::new("Cancellation discards staging before the final chunk. Once atomic commit has started, its result is awaited. Disconnect discards any uncommitted upload.").small().color(DIM));
    }
    fn results(&mut self, ui: &mut Ui) {
        ui.heading("Responses & activity");
        ui.horizontal(|ui| {
            if ui.button("Clear responses").clicked() {
                self.model.results.clear();
            }
            if ui.button("Copy activity").clicked() {
                ui.ctx().copy_text(
                    self.model
                        .log
                        .iter()
                        .cloned()
                        .collect::<Vec<_>>()
                        .join("\n"),
                );
            }
        });
        egui::ScrollArea::vertical()
            .id_salt("results")
            .show(ui, |ui| {
                for (index, (title, value)) in self.model.results.iter().enumerate().rev() {
                    ui.push_id(index, |ui| {
                        egui::CollapsingHeader::new(title)
                            .default_open(index + 1 == self.model.results.len())
                            .show(ui, |ui| {
                                let text = serde_json::to_string_pretty(value).unwrap_or_default();
                                if ui.small_button("Copy response JSON").clicked() {
                                    ui.ctx().copy_text(text.clone());
                                }
                                egui::ScrollArea::horizontal().show(ui, |ui| {
                                    ui.monospace(text);
                                });
                            });
                    });
                }
                ui.separator();
                ui.label(RichText::new("ACTIVITY").color(DIM));
                for entry in self.model.log.iter().rev() {
                    ui.label(entry);
                }
            });
    }
    fn review(&mut self, ctx: &egui::Context) {
        let Some(pending) = &self.pending else {
            return;
        };
        let (title,details,help)=match pending {
            Pending::Command(i,v)=>(self.commands[*i].title.to_owned(),serde_json::to_string_pretty(v).unwrap_or_default(),self.commands[*i].help.to_owned()),
            Pending::Upload{source,machine,destination}=>("Deploy file".into(),format!("Source: {}\nMachine: {machine}\nDestination: {destination}",source.display()),"The destination is replaced only after Control accepts every chunk and commits the upload.".into()),
        };
        let mut send = false;
        let mut cancel = false;
        egui::Modal::new(egui::Id::new("review")).show(ctx, |ui| {
            ui.set_width(520.0);
            ui.heading(title);
            ui.label(format!(
                "{}:{} · operator {}",
                self.host, self.port, self.user
            ));
            ui.label(help);
            egui::ScrollArea::both().max_height(340.0).show(ui, |ui| {
                ui.monospace(details);
            });
            ui.horizontal(|ui| {
                send = ui.button("Confirm and send").clicked();
                cancel = ui.button("Cancel").clicked();
            });
        });
        if send {
            if let Some(p) = self.pending.take() {
                match p {
                    Pending::Command(i, values) => self.send(Instruction::Send {
                        key: self.commands[i].key.into(),
                        values,
                    }),
                    Pending::Upload {
                        source,
                        machine,
                        destination,
                    } => self.send(Instruction::Upload {
                        source,
                        machine,
                        destination,
                    }),
                }
            }
        } else if cancel {
            self.pending = None;
        }
    }
}
impl eframe::App for Happy {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        for file in ctx.input(|i| i.raw.dropped_files.clone()) {
            if let Some(path) = file.path {
                self.source = path.to_string_lossy().into_owned();
                if self.destination.is_empty() {
                    self.destination = path
                        .file_name()
                        .unwrap_or_default()
                        .to_string_lossy()
                        .into_owned();
                }
                self.page = Page::Upload;
            }
        }
        self.show(ctx);
    }
}
fn form(ui: &mut Ui, fields: &[Field], value: &mut Value, manager: u32) {
    if !value.is_object() {
        *value = wire::defaults(fields);
    }
    for f in fields {
        ui.push_id(f.name, |ui| {
            let v = value
                .as_object_mut()
                .unwrap()
                .entry(f.name.to_owned())
                .or_insert_with(|| f.initial.clone());
            match &f.kind {
                Kind::Fixed(n) => {
                    ui.label(format!("{}: {n}", f.name));
                }
                Kind::Manager => {
                    ui.label(format!("{}: {manager} (session)", f.name));
                }
                Kind::Record(fields) => {
                    ui.collapsing(f.name.replace('_', " "), |ui| form(ui, fields, v, manager));
                }
                Kind::List(_, fields) => {
                    if !v.is_array() {
                        *v = json!([]);
                    }
                    let rows = v.as_array_mut().unwrap();
                    let mut remove = None;
                    egui::CollapsingHeader::new(format!(
                        "{} · {} rows",
                        f.name.replace('_', " "),
                        rows.len()
                    ))
                    .show(ui, |ui| {
                        for (i, row) in rows.iter_mut().enumerate() {
                            ui.push_id(i, |ui| {
                                ui.group(|ui| {
                                    ui.horizontal(|ui| {
                                        ui.label(format!("Row {}", i + 1));
                                        if ui.small_button("Remove").clicked() {
                                            remove = Some(i);
                                        }
                                    });
                                    form(ui, fields, row, manager);
                                });
                            });
                        }
                        if ui.small_button("Add row").clicked() {
                            rows.push(wire::defaults(fields));
                        }
                    });
                    if let Some(i) = remove {
                        rows.remove(i);
                    }
                }
                _ => {
                    let mut text = v
                        .as_str()
                        .map(str::to_owned)
                        .unwrap_or_else(|| v.to_string());
                    ui.horizontal(|ui| {
                        ui.add_sized([170.0, 20.0], egui::Label::new(f.name.replace('_', " ")));
                        if ui
                            .add(egui::TextEdit::singleline(&mut text).desired_width(
                                if matches!(f.kind, Kind::Text) {
                                    400.0
                                } else {
                                    180.0
                                },
                            ))
                            .changed()
                        {
                            *v = Value::String(text);
                        }
                    });
                }
            }
        });
    }
}
fn history(ui: &mut Ui, values: &std::collections::VecDeque<u32>) {
    ui.label(
        RichText::new("Recent users · last 120 samples")
            .small()
            .color(DIM),
    );
    let (rect, _) = ui.allocate_exact_size(
        egui::vec2(ui.available_width().min(800.0), 65.0),
        egui::Sense::hover(),
    );
    ui.painter()
        .rect_filled(rect, 4.0, Color32::from_rgb(13, 20, 31));
    let max = values.iter().copied().max().unwrap_or(1).max(1) as f32;
    let points: Vec<_> = values
        .iter()
        .enumerate()
        .map(|(i, v)| {
            egui::pos2(
                rect.left() + i as f32 / 119.0 * rect.width(),
                rect.bottom() - 4.0 - *v as f32 / max * (rect.height() - 8.0),
            )
        })
        .collect();
    if points.len() > 1 {
        ui.painter().add(egui::Shape::line(
            points,
            egui::Stroke::new(2.0_f32, ACCENT),
        ));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn all_pages_and_nested_command_forms_render() {
        let ctx = egui::Context::default();
        let mut app = Happy::new(&ctx);
        for page in [Page::Monitor, Page::Commands, Page::Upload, Page::Results] {
            app.page = page;
            let _ = ctx.run(Default::default(), |ctx| app.show(ctx));
        }
        app.page = Page::Commands;
        for i in 0..app.commands.len() {
            app.choose(i);
            let _ = ctx.run(Default::default(), |ctx| app.show(ctx));
        }
        app.pending = Some(Pending::Command(1, json!({"service_id":66305,"start":0})));
        let _ = ctx.run(Default::default(), |ctx| app.show(ctx));
    }
}
