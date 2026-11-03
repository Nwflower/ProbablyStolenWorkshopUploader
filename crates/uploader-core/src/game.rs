//! 定位游戏安装目录：环境变量优先级最高，其次读 Steam 库配置。

use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};

pub const GAME_FOLDER: &str = "Probably Stolen";
pub const APP_ID: u32 = 4348910;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameLocation {
    pub game_dir: String,
    pub mods_dir: String,
    pub source: String,
    pub exists: bool,
}

/// 返回找到的游戏目录；找不到时返回 None 与原因。
pub fn locate() -> (Option<GameLocation>, String) {
    if let Ok(custom) = std::env::var("PS_GAME_DIR") {
        let trimmed = custom.trim();
        if !trimmed.is_empty() {
            return (
                Some(make(PathBuf::from(trimmed), "环境变量 PS_GAME_DIR")),
                String::new(),
            );
        }
    }

    let steam_roots = steam_roots();
    if steam_roots.is_empty() {
        return (
            None,
            "没有找到 Steam 安装路径。请用 PS_GAME_DIR 指定游戏目录。".into(),
        );
    }

    let mut libraries: Vec<PathBuf> = Vec::new();
    for root in &steam_roots {
        let vdf = root.join("steamapps/libraryfolders.vdf");
        if let Ok(text) = fs::read_to_string(&vdf) {
            for path in parse_library_paths(&text) {
                libraries.push(PathBuf::from(path));
            }
        }
        libraries.push(root.clone());
    }

    for library in libraries {
        let candidate = library.join("steamapps/common").join(GAME_FOLDER);
        if candidate.join("Probably Stolen.exe").is_file() {
            return (Some(make(candidate, "Steam 库配置")), String::new());
        }
    }
    (
        None,
        format!(
            "在 {} 个 Steam 库里没有找到 `{GAME_FOLDER}`。可设置 PS_GAME_DIR 指向游戏目录。",
            steam_roots.len()
        ),
    )
}

fn make(dir: PathBuf, source: &str) -> GameLocation {
    let mods = dir.join("Mods");
    GameLocation {
        exists: dir.is_dir(),
        game_dir: dir.to_string_lossy().to_string(),
        mods_dir: mods.to_string_lossy().to_string(),
        source: source.to_string(),
    }
}

#[cfg(windows)]
fn steam_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Ok(path) = winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER)
        .open_subkey("Software\\Valve\\Steam")
        .and_then(|key| key.get_value::<String, _>("SteamPath"))
        && !path.trim().is_empty()
    {
        roots.push(PathBuf::from(path.replace('/', "\\")));
    }
    for env in ["ProgramFiles(x86)", "ProgramFiles"] {
        if let Ok(base) = std::env::var(env) {
            let candidate = Path::new(&base).join("Steam");
            if candidate.is_dir() && !roots.contains(&candidate) {
                roots.push(candidate);
            }
        }
    }
    roots
}

#[cfg(not(windows))]
fn steam_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Ok(home) = std::env::var("HOME") {
        let candidate = Path::new(&home).join(".steam/steam");
        if candidate.is_dir() {
            roots.push(candidate);
        }
    }
    roots
}

/// 从 libraryfolders.vdf 里抓出 "path" 值。
pub fn parse_library_paths(text: &str) -> Vec<String> {
    let mut paths = Vec::new();
    for line in text.lines() {
        let trimmed = line.trim();
        if !trimmed.starts_with("\"path\"") && !trimmed.starts_with("\"Path\"") {
            continue;
        }
        if let Some(value) = second_quoted(trimmed) {
            paths.push(value.replace("\\\\", "\\"));
        }
    }
    paths
}

fn second_quoted(line: &str) -> Option<String> {
    let mut parts = line.split('"');
    parts.next();
    parts.next()?; // key
    parts.next();
    parts.next().map(|v| v.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_library_paths() {
        let text = r#"
"libraryfolders"
{
    "0"
    {
        "path"        "C:\\Program Files (x86)\\Steam"
        "label"       ""
    }
    "1"
    {
        "path"        "E:\\SteamLibrary"
    }
}
"#;
        let paths = parse_library_paths(text);
        assert_eq!(paths.len(), 2);
        assert_eq!(paths[1], "E:\\SteamLibrary");
    }
}
