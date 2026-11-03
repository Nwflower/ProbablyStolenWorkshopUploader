//! Domain core: manifest validation, mod discovery, local memory, publish planning.
pub mod game;
pub mod manifest;
pub mod meta;
pub mod mods;

use serde::{Deserialize, Serialize};

pub const MAX_TAGS: usize = 20;
pub const ROUND_MS: u64 = 1_600;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DemoMod {
    pub id: String,
    pub name: String,
    pub subtitle: String,
    pub version: String,
    pub color: String,
    pub initial: String,
    pub path: String,
    pub title: String,
    pub description: String,
    pub tags: Vec<String>,
    pub published_id: Option<String>,
    pub valid: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Language {
    pub key: String,
    pub label: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Catalog {
    pub mode: String,
    pub version: String,
    pub mods: Vec<DemoMod>,
    pub suggested_tags: Vec<String>,
    pub languages: Vec<Language>,
}

pub fn catalog() -> Catalog {
    serde_json::from_str(include_str!("../../../tests/fixtures/catalog.json"))
        .expect("bundled demo catalog must be valid")
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub enum PublishMode {
    Full,
    Page,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum Target {
    New,
    Existing { id: String },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LanguageText {
    pub key: String,
    pub title: String,
    pub description: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PublishPlan {
    pub mod_id: String,
    pub target: Target,
    pub mode: PublishMode,
    pub languages: Vec<LanguageText>,
    pub tags: Vec<String>,
    pub keep_tags: bool,
    pub keep_description: bool,
    /// None preserves the existing visibility; new items must be private (2) in this preview.
    pub visibility: Option<u8>,
    pub change_note: String,
    /// 为空表示本次不设置内容目录（仅更新页面）。
    #[serde(default)]
    pub content_folder: String,
    #[serde(default)]
    pub preview_path: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Issue {
    pub field: String,
    pub message: String,
}

pub fn normalize_tags(tags: &[String]) -> Vec<String> {
    let mut normalized: Vec<String> = Vec::new();
    for tag in tags
        .iter()
        .map(|tag| tag.trim())
        .filter(|tag| !tag.is_empty())
    {
        if !normalized
            .iter()
            .any(|old| old.to_lowercase() == tag.to_lowercase())
        {
            normalized.push(tag.to_owned());
        }
    }
    normalized
}

pub fn validate_plan(plan: &PublishPlan) -> Vec<Issue> {
    let catalog = catalog();
    let mut issues = Vec::new();
    let mut issue = |field: &str, message: &str| {
        issues.push(Issue {
            field: field.into(),
            message: message.into(),
        })
    };
    match catalog.mods.iter().find(|m| m.id == plan.mod_id) {
        None => issue("modId", "请选择演示模组。"),
        Some(m) if !m.valid => issue("modId", "演示清单缺少 Assemblies，修复后才能发布。"),
        _ => (),
    }
    match &plan.target {
        Target::New => {
            if plan.mode == PublishMode::Page {
                issue("mode", "新建物品需要完整发布。")
            }
            if plan.visibility != Some(2) {
                issue("visibility", "新建演示物品默认隐藏。")
            }
            if plan.keep_tags || plan.keep_description {
                issue("target", "新建物品不能保留尚不存在的页面字段。")
            }
        }
        Target::Existing { id } => {
            if id.is_empty()
                || !id.bytes().all(|c| c.is_ascii_digit())
                || id.parse::<u64>().ok().filter(|v| *v > 0).is_none()
            {
                issue("target", "工坊 ID 必须为有效的非零 64 位十进制字符串。")
            }
        }
    }
    if plan.visibility.is_some_and(|v| v > 3) {
        issue("visibility", "请选择有效的可见性。")
    }
    if !plan.keep_tags && normalize_tags(&plan.tags).len() > MAX_TAGS {
        issue("tags", "TAG 最多 20 个，请删减后重试。")
    }
    let mut keys: Vec<String> = Vec::new();
    for lang in &plan.languages {
        if !catalog.languages.iter().any(|v| v.key == lang.key) || keys.contains(&lang.key) {
            issue("languages", "语言无效或重复。");
        }
        keys.push(lang.key.clone());
        if lang.title.trim().is_empty() {
            issue("languages", "每个待发布语言都需要填写标题。")
        }
        if lang.title.contains('\0') || lang.description.contains('\0') {
            issue("languages", "文案不能包含空字符。")
        }
    }
    if !plan
        .languages
        .iter()
        .any(|v| v.key == "english" && !v.title.trim().is_empty())
    {
        issue("languages", "请填写英文标题，英文作为首个发布语言。");
    }
    issues
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Round {
    pub language: String,
    pub upload_content: bool,
    pub set_tags: bool,
    pub tags: Vec<String>,
    pub set_description: bool,
    pub visibility: Option<u8>,
}

pub fn rounds(plan: &PublishPlan) -> Result<Vec<Round>, Vec<Issue>> {
    let issues = validate_plan(plan);
    if !issues.is_empty() {
        return Err(issues);
    }
    let mut languages = plan.languages.clone();
    languages.sort_by_key(|l| l.key != "english");
    Ok(languages
        .iter()
        .enumerate()
        .map(|(index, lang)| Round {
            language: lang.key.clone(),
            upload_content: index == 0 && plan.mode == PublishMode::Full,
            set_tags: index == 0 && !plan.keep_tags,
            tags: if index == 0 && !plan.keep_tags {
                normalize_tags(&plan.tags)
            } else {
                vec![]
            },
            set_description: !plan.keep_description && !lang.description.trim().is_empty(),
            visibility: if index == 0 { plan.visibility } else { None },
        })
        .collect())
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub enum Scenario {
    Success,
    Failure,
    Unconfirmed,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub enum TaskStatus {
    Running,
    Succeeded,
    Failed,
    Unconfirmed,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TaskSnapshot {
    pub id: String,
    pub status: TaskStatus,
    pub progress: u8,
    pub round: usize,
    pub total_rounds: usize,
    pub language: String,
    pub message: String,
    pub confirmed_languages: Vec<String>,
    pub logs: Vec<String>,
}

/// Deterministic simulation: callers supply elapsed monotonic time. No native calls.
pub struct Simulation {
    id: String,
    rounds: Vec<Round>,
    scenario: Scenario,
}

impl Simulation {
    pub fn new(id: String, plan: &PublishPlan, scenario: Scenario) -> Result<Self, Vec<Issue>> {
        Ok(Self {
            id,
            rounds: rounds(plan)?,
            scenario,
        })
    }

    pub fn snapshot(&self, elapsed_ms: u64) -> TaskSnapshot {
        let total = self.rounds.len();
        let fail_round = if total > 1 { 1 } else { 0 };
        let stop_ms = (fail_round as u64) * ROUND_MS + 1_000;
        let unsuccessful = self.scenario != Scenario::Success && elapsed_ms >= stop_ms;
        let elapsed = if unsuccessful {
            stop_ms
        } else {
            elapsed_ms.min(total as u64 * ROUND_MS)
        };
        let confirmed = (elapsed / ROUND_MS) as usize;
        let index = confirmed.min(total - 1);
        let (status, message) = if unsuccessful {
            match self.scenario {
                Scenario::Failure => (TaskStatus::Failed, "模拟：字段设置失败，本轮未提交。"),
                _ => (
                    TaskStatus::Unconfirmed,
                    "模拟：等待超时，结果未确认；请先核对，再决定是否重试。",
                ),
            }
        } else if confirmed == total {
            (
                TaskStatus::Succeeded,
                "模拟发布完成。未连接 Steam，也未上传任何文件。",
            )
        } else if elapsed % ROUND_MS < 600 {
            (TaskStatus::Running, "模拟：准备当前语言的页面字段…")
        } else {
            (TaskStatus::Running, "模拟：等待提交结果…")
        };
        let mut logs = vec!["[模拟] 发布计划已冻结；不会调用 Steam。".into()];
        if self.rounds[0].upload_content {
            logs.push("[模拟] 首轮计划包含内容文件。".into());
        } else {
            logs.push("[模拟] 仅更新页面：不设置内容目录。".into());
        }
        if self.rounds[0].set_tags {
            logs.push(format!(
                "[模拟] 首轮覆盖 TAG（{} 个）。",
                self.rounds[0].tags.len()
            ));
        } else {
            logs.push("[模拟] 保留已有 TAG。".into());
        }
        for round in self.rounds.iter().take(confirmed) {
            logs.push(format!("[模拟] {} 已确认。", round.language));
        }
        if unsuccessful {
            logs.push(message.into());
        }
        TaskSnapshot {
            id: self.id.clone(),
            status,
            progress: ((elapsed * 100) / (total as u64 * ROUND_MS)) as u8,
            round: index + 1,
            total_rounds: total,
            language: self.rounds[index].language.clone(),
            message: message.into(),
            confirmed_languages: self
                .rounds
                .iter()
                .take(confirmed)
                .map(|r| r.language.clone())
                .collect(),
            logs,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn plan() -> PublishPlan {
        PublishPlan {
            mod_id: "demo_details".into(),
            target: Target::Existing {
                id: "9007199254740993".into(),
            },
            mode: PublishMode::Full,
            languages: vec![
                LanguageText {
                    key: "schinese".into(),
                    title: "名称".into(),
                    description: "文案".into(),
                },
                LanguageText {
                    key: "english".into(),
                    title: "Name".into(),
                    description: "Description".into(),
                },
            ],
            tags: vec!["Mod".into()],
            keep_tags: true,
            keep_description: true,
            visibility: None,
            change_note: String::new(),
            content_folder: String::new(),
            preview_path: String::new(),
        }
    }
    #[test]
    fn preserves_existing_fields_and_id_precision() {
        let p = plan();
        let json = serde_json::to_string(&p).unwrap();
        assert!(json.contains("\"9007199254740993\""));
        let r = rounds(&p).unwrap();
        assert_eq!(r[0].language, "english");
        assert!(
            r.iter()
                .all(|r| !r.set_tags && !r.set_description && r.visibility.is_none())
        );
    }
    #[test]
    fn writes_item_fields_only_once() {
        let mut p = plan();
        p.keep_tags = false;
        p.tags = vec![" Mod ".into(), "mod".into(), "UI".into()];
        p.visibility = Some(2);
        let r = rounds(&p).unwrap();
        assert_eq!(r[0].tags, vec!["Mod", "UI"]);
        assert!(r[0].upload_content && r[0].set_tags);
        assert!(!r[1].upload_content && !r[1].set_tags && r[1].visibility.is_none());
    }
    #[test]
    fn page_update_never_uploads_content() {
        let mut p = plan();
        p.mode = PublishMode::Page;
        assert!(rounds(&p).unwrap().iter().all(|r| !r.upload_content));
    }
    #[test]
    fn explicit_empty_tags_means_clear() {
        let mut p = plan();
        p.keep_tags = false;
        p.tags.clear();
        let r = rounds(&p).unwrap();
        assert!(r[0].set_tags && r[0].tags.is_empty());
    }
    #[test]
    fn rejects_invalid_target_and_new_page_update() {
        let mut p = plan();
        p.target = Target::Existing {
            id: "18446744073709551616".into(),
        };
        assert!(rounds(&p).is_err());
        p.target = Target::New;
        p.mode = PublishMode::Page;
        assert!(rounds(&p).is_err());
    }
    #[test]
    fn rejects_invalid_mod_and_missing_english() {
        let mut p = plan();
        p.mod_id = "demo_invalid".into();
        assert!(rounds(&p).is_err());
        p = plan();
        p.languages.retain(|l| l.key != "english");
        assert!(rounds(&p).is_err());
    }
    #[test]
    fn protects_tag_limit() {
        let mut p = plan();
        p.keep_tags = false;
        p.tags = (0..21).map(|i| i.to_string()).collect();
        assert!(rounds(&p).is_err());
    }
    #[test]
    fn partial_failure_retains_confirmed_language() {
        let s = Simulation::new("test".into(), &plan(), Scenario::Failure).unwrap();
        let result = s.snapshot(99_000);
        assert_eq!(result.status, TaskStatus::Failed);
        assert_eq!(result.confirmed_languages, vec!["english"]);
        assert!(result.progress < 100);
    }
    #[test]
    fn timeout_is_neither_failure_nor_success() {
        let s = Simulation::new("test".into(), &plan(), Scenario::Unconfirmed).unwrap();
        assert_eq!(s.snapshot(99_000).status, TaskStatus::Unconfirmed);
    }
    #[test]
    fn success_confirms_all_rounds() {
        let s = Simulation::new("test".into(), &plan(), Scenario::Success).unwrap();
        let r = s.snapshot(99_000);
        assert_eq!(r.status, TaskStatus::Succeeded);
        assert_eq!(r.progress, 100);
        assert_eq!(r.confirmed_languages.len(), 2);
    }
}
