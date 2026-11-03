//! 本地记忆文件：与旧版 C# 上传器的 `workshop-meta.json` 结构兼容。

use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct LangText {
    #[serde(rename = "Title", default)]
    pub title: String,
    #[serde(rename = "Desc", default)]
    pub desc: String,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ModMeta {
    #[serde(rename = "PublishedId", default)]
    pub published_id: u64,
    #[serde(rename = "Visibility", default)]
    pub visibility: i32,
    #[serde(rename = "PreviewPath", default)]
    pub preview_path: String,
    #[serde(rename = "Languages", default)]
    pub languages: BTreeMap<String, LangText>,
    #[serde(rename = "PendingFirstPublish", default)]
    pub pending_first_publish: bool,
    #[serde(rename = "Tags", default)]
    pub tags: Vec<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Root {
    #[serde(rename = "Mods", default)]
    mods: BTreeMap<String, ModMeta>,
    #[serde(rename = "CommunityNoticeSeen", default)]
    community_notice_seen: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MetaSummary {
    pub path: String,
    pub mod_count: usize,
    pub community_notice_seen: bool,
}

pub struct MetaStore {
    path: PathBuf,
    root: Root,
    pub last_error: String,
}

impl MetaStore {
    pub fn open(dir: Option<&Path>) -> Self {
        let dir = dir.map(|p| p.to_path_buf()).unwrap_or_else(default_dir);
        let _ = fs::create_dir_all(&dir);
        let path = dir.join("workshop-meta.json");
        let mut store = Self {
            path,
            root: Root::default(),
            last_error: String::new(),
        };
        store.load();
        store
    }

    pub fn file_path(&self) -> String {
        self.path.to_string_lossy().to_string()
    }

    pub fn summary(&self) -> MetaSummary {
        MetaSummary {
            path: self.file_path(),
            mod_count: self.root.mods.len(),
            community_notice_seen: self.root.community_notice_seen,
        }
    }

    pub fn get(&self, mod_id: &str) -> ModMeta {
        self.root.mods.get(mod_id).cloned().unwrap_or_default()
    }

    pub fn remember_published(&mut self, mod_id: &str, id: u64) -> bool {
        if mod_id.is_empty() || id == 0 {
            return false;
        }
        let entry = self.root.mods.entry(mod_id.to_string()).or_default();
        entry.published_id = id;
        entry.pending_first_publish = false;
        self.save()
    }

    pub fn merge(&mut self, other: Root) -> usize {
        let mut added = 0;
        for (key, value) in other.mods {
            if let std::collections::btree_map::Entry::Vacant(entry) = self.root.mods.entry(key) {
                entry.insert(value);
                added += 1;
            }
        }
        if !self.root.community_notice_seen {
            self.root.community_notice_seen = other.community_notice_seen;
        }
        added
    }

    pub fn save(&mut self) -> bool {
        let temp = self
            .path
            .with_extension(format!("tmp{}", std::process::id()));
        let json = match serde_json::to_vec_pretty(&self.root) {
            Ok(v) => v,
            Err(e) => {
                self.last_error = format!("序列化记忆失败：{e}");
                return false;
            }
        };
        let result = fs::write(&temp, json).and_then(|_| fs::rename(&temp, &self.path));
        match result {
            Ok(()) => {
                self.last_error.clear();
                true
            }
            Err(e) => {
                let _ = fs::remove_file(&temp);
                self.last_error = format!("写入记忆失败：{e}");
                false
            }
        }
    }

    fn load(&mut self) {
        let Ok(text) = fs::read_to_string(&self.path) else {
            return;
        };
        match serde_json::from_str::<Root>(&text) {
            Ok(root) => self.root = root,
            Err(e) => self.last_error = format!("记忆文件无法解析，已从空记录开始：{e}"),
        }
    }
}

pub fn default_dir() -> PathBuf {
    if let Ok(custom) = std::env::var("PSMOD_UPLOADER_META_DIR")
        && !custom.trim().is_empty()
    {
        return PathBuf::from(custom);
    }
    let appdata = std::env::var("APPDATA").unwrap_or_default();
    PathBuf::from(appdata).join("ProbablyStolenWorkshopUploader")
}

/// 从旧版文件导入：只读解析，不修改原文件。
pub fn read_legacy(path: &Path) -> Result<(usize, Root), String> {
    let text = fs::read_to_string(path).map_err(|e| format!("读取失败：{e}"))?;
    let root: Root = serde_json::from_str(&text).map_err(|e| format!("解析失败：{e}"))?;
    Ok((root.mods.len(), root))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_legacy_shape_and_roundtrips() {
        let dir = std::env::temp_dir().join(format!("ps-meta-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).unwrap();
        let legacy = r#"{"Mods":{"demo":{"PublishedId":9007199254740993,"Visibility":0,"PreviewPath":"p.png","Languages":{"english":{"Title":"T","Desc":"D"}},"Tags":["Mod"]}}}"#;
        fs::write(dir.join("workshop-meta.json"), legacy).unwrap();

        let mut store = MetaStore::open(Some(&dir));
        let meta = store.get("demo");
        assert_eq!(meta.published_id, 9007199254740993);
        assert_eq!(meta.languages["english"].title, "T");
        assert_eq!(meta.tags, vec!["Mod".to_string()]);

        assert!(store.remember_published("other", 42));
        let reopened = MetaStore::open(Some(&dir));
        assert_eq!(reopened.get("other").published_id, 42);
        assert_eq!(reopened.get("demo").published_id, 9007199254740993);
        let _ = fs::remove_dir_all(&dir);
    }
}
