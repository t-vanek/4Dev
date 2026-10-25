#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod ui;
fn main() -> eframe::Result {
    eframe::run_native(
        "Happy · 4Dev",
        eframe::NativeOptions {
            viewport: eframe::egui::ViewportBuilder::default()
                .with_inner_size([1280.0, 820.0])
                .with_min_inner_size([920.0, 600.0]),
            ..Default::default()
        },
        Box::new(|cc| Ok(Box::new(ui::Happy::new(&cc.egui_ctx)))),
    )
}
