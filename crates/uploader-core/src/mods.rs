//! 本地模组发现：只扫描 `<游戏>/Mods/*`，不列出工坊订阅副本。

use crate::manifest::{Issue, Level, ManifestReport, validate_package};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalMod {
    pub id: String,
    pub folder: String,
    pub name: String,
    pub author: String,
    pub version: String,
    pub description: String,
    pub path: String,
    pub package_root: String,
    pub assemblies: Vec<String>,
    pub capabilities: Vec<String>,
    pub errors: usize,
    pub warnings: usize,
    pub issues: Vec<Issue>,
    pub has_preview: bool,
    pub cover: Option<String>,
    #[serde(default)]
    pub published_id: Option<String>,
    #[serde(default)]
    pub saved_tags: Vec<String>,
    #[serde(default)]
    pub saved_preview: String,
}

impl LocalMod {
    pub fn valid(&self) -> bool {
        self.errors == 0
    }
}

pub fn discover(mods_root: &Path) -> Vec<LocalMod> {
    let Ok(entries) = fs::read_dir(mods_root) else {
        return Vec::new();
    };
    let mut mods: Vec<LocalMod> = Vec::new();
    for entry in entries.flatten() {
        let path = entry.path();
        if !path.is_dir() || !path.join("manifest.xml").is_file() {
            continue;
        }
        mods.push(from_report(&path, &validate_package(&path)));
    }
    mods.sort_by_key(|item| item.name.to_lowercase());
    mods
}

fn from_report(dir: &Path, report: &ManifestReport) -> LocalMod {
    let preview = pick_preview(dir);
    LocalMod {
        id: if report.manifest.id.is_empty() {
            dir.file_name()
                .unwrap_or_default()
                .to_string_lossy()
                .to_string()
        } else {
            report.manifest.id.clone()
        },
        folder: dir
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .to_string(),
        name: if report.manifest.name.is_empty() {
            dir.file_name()
                .unwrap_or_default()
                .to_string_lossy()
                .to_string()
        } else {
            report.manifest.name.clone()
        },
        author: report.manifest.author.clone(),
        version: report.manifest.version.clone(),
        description: report.manifest.description.clone(),
        path: dir.to_string_lossy().to_string(),
        package_root: report.package_root.clone(),
        assemblies: report.manifest.assemblies.clone(),
        capabilities: report.manifest.capabilities.clone(),
        errors: report
            .issues
            .iter()
            .filter(|i| i.level == Level::Error)
            .count(),
        warnings: report
            .issues
            .iter()
            .filter(|i| i.level == Level::Warning)
            .count(),
        issues: report.issues.clone(),
        has_preview: preview.is_some(),
        cover: preview.map(|p| p.to_string_lossy().to_string()),
        published_id: None,
        saved_tags: Vec::new(),
        saved_preview: String::new(),
    }
}

fn pick_preview(dir: &Path) -> Option<PathBuf> {
    for name in ["preview.png", "preview.jpg", "preview.jpeg"] {
        let candidate = dir.join(name);
        if candidate.is_file() {
            return Some(candidate);
        }
    }
    None
}

/// 读取 `<游戏>/Mods` 下的某个具体目录；用于用户手动选择文件夹。
pub fn load_one(dir: &Path) -> Option<LocalMod> {
    if !dir.join("manifest.xml").is_file() {
        return None;
    }
    Some(from_report(dir, &validate_package(dir)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn discovers_only_directories_with_manifest() {
        let root = std::env::temp_dir().join(format!("ps-mods-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(root.join("good")).unwrap();
        fs::write(
            root.join("good/manifest.xml"),
            r#"<Manifest><ID>good_mod</ID><Name>Good</Name><Author>A</Author><ModVersion>1.0.0</ModVersion><Description>D</Description><Assemblies><Assembly>Good.dll</Assembly></Assemblies><Capabilities /></Manifest>"#,
        )
        .unwrap();
        fs::write(root.join("good/Good.dll"), b"xx IMod xx").unwrap();
        fs::create_dir_all(root.join("empty")).unwrap();

        let mods = discover(&root);
        assert_eq!(mods.len(), 1);
        assert_eq!(mods[0].id, "good_mod");
        assert!(mods[0].valid());
        let _ = fs::remove_dir_all(&root);
    }
}
