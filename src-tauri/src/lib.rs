use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::Mutex;

use base64::Engine;
use base64::engine::general_purpose::STANDARD as BASE64;
use steam_backend::{Event, SteamStatus, SteamWorker};
use tauri::{Emitter, Manager};
use uploader_core::PublishPlan;
use uploader_core::game::{self, GameLocation};
use uploader_core::meta::{self, MetaStore, MetaSummary};
use uploader_core::mods::{self, LocalMod};

struct AppState {
    steam: SteamWorker,
    meta: Mutex<MetaStore>,
}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
struct ModsView {
    game: Option<GameLocation>,
    mods: Vec<LocalMod>,
    previews: BTreeMap<String, String>,
    error: String,
}

/// 只把本次扫描到的模组预览或该模组已记忆的预览读入本地页面。
/// 前端不接受任意文件路径，因此不需要开放全局 asset protocol 范围。
fn preview_data(item: &LocalMod) -> Option<String> {
    let candidates = [
        PathBuf::from(&item.saved_preview),
        item.cover.as_ref().map(PathBuf::from).unwrap_or_default(),
    ];
    let path = candidates.iter().find(|path| path.is_file())?;
    let mime = match path.extension()?.to_str()?.to_ascii_lowercase().as_str() {
        "png" => "image/png",
        "jpg" | "jpeg" => "image/jpeg",
        _ => return None,
    };
    let metadata = std::fs::metadata(path).ok()?;
    if metadata.len() > 10 * 1024 * 1024 {
        return None;
    }
    let bytes = std::fs::read(path).ok()?;
    Some(format!("data:{mime};base64,{}", BASE64.encode(bytes)))
}

#[tauri::command]
fn steam_status(state: tauri::State<'_, AppState>) -> Option<SteamStatus> {
    state.steam.status()
}

#[tauri::command]
fn list_mods(state: tauri::State<'_, AppState>) -> ModsView {
    let (location, error) = game::locate();
    let mut found = location
        .as_ref()
        .map(|g| mods::discover(std::path::Path::new(&g.mods_dir)))
        .unwrap_or_default();
    if let Ok(store) = state.meta.lock() {
        for item in found.iter_mut() {
            let saved = store.get(&item.id);
            if saved.published_id > 0 {
                item.published_id = Some(saved.published_id.to_string());
            }
            item.saved_tags = saved.tags.clone();
            item.saved_preview = saved.preview_path.clone();
        }
    }
    let previews = found
        .iter()
        .filter_map(|item| preview_data(item).map(|data| (item.id.clone(), data)))
        .collect();
    ModsView {
        game: location,
        mods: found,
        previews,
        error,
    }
}

#[tauri::command]
fn query_items(id: String, state: tauri::State<'_, AppState>) {
    state.steam.query(id);
}

#[tauri::command]
fn publish(plan: PublishPlan, state: tauri::State<'_, AppState>) {
    state.steam.publish(plan);
}

#[tauri::command]
fn meta_summary(state: tauri::State<'_, AppState>) -> MetaSummary {
    state
        .meta
        .lock()
        .map(|store| store.summary())
        .unwrap_or(MetaSummary {
            path: meta::default_dir().to_string_lossy().to_string(),
            mod_count: 0,
            community_notice_seen: false,
        })
}

#[tauri::command]
fn remember_published(mod_id: String, id: String, state: tauri::State<'_, AppState>) -> bool {
    let Ok(parsed) = id.parse::<u64>() else {
        return false;
    };
    state
        .meta
        .lock()
        .map(|mut store| store.remember_published(&mod_id, parsed))
        .unwrap_or(false)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let meta = MetaStore::open(None);
    tauri::Builder::default()
        .setup(|app| {
            let handle = app.handle().clone();
            let worker = SteamWorker::spawn(move |event| {
                let _ = handle.emit("steam-event", &event);
            });
            app.manage(AppState {
                steam: worker,
                meta: Mutex::new(meta),
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            steam_status,
            list_mods,
            query_items,
            publish,
            meta_summary,
            remember_published
        ])
        .run(tauri::generate_context!())
        .expect("failed to run Workshop Uploader");
}

#[allow(dead_code)]
fn _assert_event_serializable(event: Event) -> Event {
    event
}
