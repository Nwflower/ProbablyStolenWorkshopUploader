#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    if std::env::args().any(|arg| arg == "--selftest") {
        attach_console();
        std::process::exit(steam_backend::selftest());
    }
    if std::env::args().any(|arg| arg == "--querytest") {
        attach_console();
        std::process::exit(steam_backend::querytest());
    }
    if std::env::args().any(|arg| arg == "--scanmods") {
        attach_console();
        std::process::exit(scan_mods());
    }
    workshop_uploader_lib::run()
}

#[cfg(windows)]
fn attach_console() {
    unsafe extern "system" {
        fn AttachConsole(dw_process_id: u32) -> i32;
    }
    const ATTACH_PARENT_PROCESS: u32 = 0xFFFF_FFFF;
    unsafe {
        AttachConsole(ATTACH_PARENT_PROCESS);
    }
}

#[cfg(not(windows))]
fn attach_console() {}

fn scan_mods() -> i32 {
    let (location, error) = uploader_core::game::locate();
    let Some(location) = location else {
        println!("NOT FOUND: {error}");
        return 2;
    };
    println!("game={}", location.game_dir);
    for item in uploader_core::mods::discover(std::path::Path::new(&location.mods_dir)) {
        println!(
            "{} | {} | v{} | errors={} warnings={} | {}",
            item.id, item.name, item.version, item.errors, item.warnings, item.folder
        );
        for issue in &item.issues {
            println!("  [{:?}] {}: {}", issue.level, issue.element, issue.message);
        }
    }
    0
}
