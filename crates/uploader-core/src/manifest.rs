//! manifest.xml 解析与校验，判据对齐游戏 `ModLoader` 的加载规则。

use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::fs;
use std::path::{Path, PathBuf};

pub const CAPABILITIES: [&str; 7] = [
    "files",
    "network",
    "process",
    "dynamic-code",
    "native",
    "reflection-external",
    "steam",
];

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum Level {
    Error,
    Warning,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Issue {
    pub level: Level,
    pub element: String,
    pub message: String,
    pub fix: String,
    pub line: u64,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Manifest {
    pub id: String,
    pub name: String,
    pub author: String,
    pub version: String,
    pub description: String,
    pub assemblies: Vec<String>,
    pub capabilities: Vec<String>,
    pub game_versions: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ManifestReport {
    pub manifest: Manifest,
    pub issues: Vec<Issue>,
    pub package_root: String,
}

impl ManifestReport {
    pub fn has_errors(&self) -> bool {
        self.issues.iter().any(|i| i.level == Level::Error)
    }
}

struct Element {
    name: String,
    text: String,
    line: u64,
    children: Vec<Element>,
}

fn parse_tree(xml: &str) -> Result<Element, String> {
    use quick_xml::Reader;
    use quick_xml::events::Event;

    let mut reader = Reader::from_str(xml);
    reader.config_mut().trim_text(true);
    let mut stack: Vec<Element> = vec![Element {
        name: String::new(),
        text: String::new(),
        line: 1,
        children: Vec::new(),
    }];
    loop {
        let before = reader.buffer_position();
        match reader.read_event() {
            Ok(Event::Start(e)) => {
                let name = String::from_utf8_lossy(e.name().as_ref()).to_string();
                stack.push(Element {
                    name,
                    text: String::new(),
                    line: line_of(xml, before),
                    children: Vec::new(),
                });
            }
            Ok(Event::Empty(e)) => {
                let name = String::from_utf8_lossy(e.name().as_ref()).to_string();
                if let Some(parent) = stack.last_mut() {
                    parent.children.push(Element {
                        name,
                        text: String::new(),
                        line: line_of(xml, before),
                        children: Vec::new(),
                    });
                }
            }
            Ok(Event::Text(e)) => {
                if let Some(current) = stack.last_mut() {
                    current.text.push_str(&String::from_utf8_lossy(e.as_ref()));
                }
            }
            Ok(Event::CData(e)) => {
                if let Some(current) = stack.last_mut() {
                    current.text.push_str(&String::from_utf8_lossy(e.as_ref()));
                }
            }
            Ok(Event::End(_)) => {
                if stack.len() <= 1 {
                    return Err("出现了多余的结束标签。".into());
                }
                let done = stack.pop().expect("depth checked");
                if let Some(parent) = stack.last_mut() {
                    parent.children.push(done);
                }
            }
            Ok(Event::Eof) => break,
            Err(e) => return Err(format!("XML 解析失败：{e}")),
            _ => {}
        }
    }
    if stack.len() != 1 {
        return Err("有标签没有闭合。".into());
    }
    stack
        .pop()
        .and_then(|root| root.children.into_iter().next())
        .ok_or_else(|| "manifest.xml 没有根元素。".into())
}

fn line_of(xml: &str, byte_pos: u64) -> u64 {
    let end = (byte_pos.min(xml.len() as u64)) as usize;
    (xml[..end].bytes().filter(|b| *b == b'\n').count() as u64) + 1
}

fn child<'a>(el: &'a Element, name: &str) -> Option<&'a Element> {
    el.children.iter().find(|c| c.name == name)
}

fn text_of(el: &Element, name: &str) -> String {
    child(el, name)
        .map(|c| c.text.trim().to_string())
        .unwrap_or_default()
}

fn valid_id(id: &str) -> Result<(), String> {
    if id.is_empty() {
        return Err("ID 不能为空。".into());
    }
    if id.len() > 64 {
        return Err("ID 超过 64 个字符。".into());
    }
    if id.starts_with('.') {
        return Err("ID 不能以 . 开头。".into());
    }
    for (index, ch) in id.char_indices() {
        let ok = ch.is_ascii_alphanumeric() || matches!(ch, '_' | '-' | '.');
        if !ok {
            return Err(format!(
                "第 {} 个字符 `{ch}` 不合法，只允许字母、数字、_、-、.。",
                index + 1
            ));
        }
    }
    Ok(())
}

fn invalid_assembly_path(path: &str) -> Option<String> {
    if path.is_empty() {
        return Some("程序集路径为空。".into());
    }
    let normalized = path.replace('\\', "/");
    if normalized.contains("..") {
        return Some("路径不能包含 ..".into());
    }
    if normalized.starts_with('/') || path.chars().nth(1) == Some(':') {
        return Some("必须使用包内相对路径，不能是绝对路径".into());
    }
    if !normalized.to_ascii_lowercase().ends_with(".dll") {
        return Some("必须是 .dll 文件".into());
    }
    None
}

/// 粗略判断二进制里是否出现 IMod 标记；这是启发式，不代表真的实现了接口。
fn contains_imod_marker(path: &Path) -> bool {
    match fs::read(path) {
        Ok(bytes) => bytes.windows(4).any(|w| w == b"IMod"),
        Err(_) => false,
    }
}

/// 校验一个模组目录。目录下必须有 manifest.xml。
pub fn validate_package(dir: &Path) -> ManifestReport {
    let manifest_path = dir.join("manifest.xml");
    let mut issues = Vec::new();
    let mut manifest = Manifest::default();

    let xml = match fs::read_to_string(&manifest_path) {
        Ok(text) => Some(text),
        Err(_) => {
            issues.push(Issue {
                level: Level::Error,
                element: "manifest.xml".into(),
                message: "找不到或无法读取 manifest.xml。".into(),
                fix: "把它放在模组包根目录，并确保是 UTF-8 文本。".into(),
                line: 0,
            });
            None
        }
    };

    if let Some(xml) = xml {
        match parse_tree(&xml) {
            Err(message) => issues.push(Issue {
                level: Level::Error,
                element: "manifest.xml".into(),
                message,
                fix: "检查标签是否成对闭合、属性值是否加引号。".into(),
                line: 0,
            }),
            Ok(root) => {
                if root.name.contains(':') {
                    issues.push(Issue {
                        level: Level::Error,
                        element: root.name.clone(),
                        message: "根节点带有 XML 命名空间，游戏会把字段当成缺失。".into(),
                        fix: "去掉 xmlns 与命名空间前缀。".into(),
                        line: root.line,
                    });
                }
                manifest.id = text_of(&root, "ID");
                manifest.name = text_of(&root, "Name");
                manifest.author = text_of(&root, "Author");
                manifest.version = text_of(&root, "ModVersion");
                manifest.description = text_of(&root, "Description");

                if let Err(message) = valid_id(&manifest.id) {
                    issues.push(Issue {
                        level: Level::Error,
                        element: "<ID>".into(),
                        message,
                        fix: "使用字母、数字、_、-、. 组成的稳定 ID。".into(),
                        line: child(&root, "ID").map(|e| e.line).unwrap_or(0),
                    });
                }

                for optional in ["Name", "Author", "ModVersion", "Description"] {
                    if text_of(&root, optional).is_empty() {
                        issues.push(Issue {
                            level: Level::Warning,
                            element: format!("<{optional}>"),
                            message: format!("<{optional}> 为空，工坊页面会缺这一项。"),
                            fix: "补上实际内容；它不影响游戏加载。".into(),
                            line: 0,
                        });
                    }
                }

                match child(&root, "Assemblies") {
                    None => issues.push(Issue {
                        level: Level::Error,
                        element: "<Assemblies>".into(),
                        message: "缺少 <Assemblies>，游戏不会加载任何代码。".into(),
                        fix: "至少写一个 <Assembly>你的DLL.dll</Assembly>。".into(),
                        line: 0,
                    }),
                    Some(assemblies) => {
                        let mut entries = Vec::new();
                        for item in &assemblies.children {
                            if item.name != "Assembly" {
                                issues.push(Issue {
                                    level: Level::Error,
                                    element: "<Assemblies>".into(),
                                    message: format!("子元素 <{}> 不会被识别。", item.name),
                                    fix: "每一项都写成 <Assembly>。".into(),
                                    line: item.line,
                                });
                                continue;
                            }
                            let value = item.text.trim().to_string();
                            if let Some(problem) = invalid_assembly_path(&value) {
                                issues.push(Issue {
                                    level: Level::Error,
                                    element: "<Assembly>".into(),
                                    message: format!("`{value}`：{problem}"),
                                    fix: "改成包内相对 .dll 路径。".into(),
                                    line: item.line,
                                });
                            } else {
                                entries.push(value);
                            }
                        }
                        if entries.is_empty() {
                            issues.push(Issue {
                                level: Level::Error,
                                element: "<Assemblies>/<Assembly>".into(),
                                message: "没有有效的程序集项。".into(),
                                fix: "至少列出一个存在的 DLL。".into(),
                                line: assemblies.line,
                            });
                        }
                        let mut any_marker = false;
                        for entry in &entries {
                            let full = dir.join(entry.replace('\\', "/"));
                            if !full.is_file() {
                                issues.push(Issue {
                                    level: Level::Error,
                                    element: "<Assembly>".into(),
                                    message: format!("声明的 DLL 不存在：{entry}"),
                                    fix: "按包内实际路径修正，或把文件放进来。".into(),
                                    line: assemblies.line,
                                });
                            } else if contains_imod_marker(&full) {
                                any_marker = true;
                            }
                        }
                        if !entries.is_empty() && !any_marker {
                            issues.push(Issue {
                                level: Level::Error,
                                element: "<Assemblies>/<Assembly>".into(),
                                message: "没有任何列出的 DLL 包含 IMod 标记，游戏会拒绝启用。"
                                    .into(),
                                fix: "确认入口 DLL 里有 IMod 实现。".into(),
                                line: assemblies.line,
                            });
                        }
                        manifest.assemblies = entries;
                    }
                }

                match child(&root, "Capabilities") {
                    None => issues.push(Issue {
                        level: Level::Error,
                        element: "<Capabilities>".into(),
                        message: "缺少 <Capabilities>（可以为空元素）。".into(),
                        fix: "写 <Capabilities /> 或列出实际使用的能力。".into(),
                        line: 0,
                    }),
                    Some(caps) => {
                        let mut list = Vec::new();
                        for item in &caps.children {
                            // 游戏读取的是子元素文本，例如 <Capability>files</Capability>。
                            let name = item.text.trim().to_string();
                            if name.is_empty() {
                                continue;
                            }
                            if !CAPABILITIES.contains(&name.as_str()) {
                                issues.push(Issue {
                                    level: Level::Error,
                                    element: "<Capabilities>".into(),
                                    message: format!("未知能力 `{name}`。"),
                                    fix: format!("只能是：{}", CAPABILITIES.join(" / ")),
                                    line: item.line,
                                });
                            } else {
                                list.push(name);
                            }
                        }
                        manifest.capabilities = list;
                    }
                }

                if let Some(versions) = child(&root, "GameVersions") {
                    manifest.game_versions = versions
                        .children
                        .iter()
                        .filter(|c| c.name == "Version")
                        .map(|c| c.text.trim().to_string())
                        .filter(|v| !v.is_empty())
                        .collect();
                }
            }
        }
    }

    // 未声明的 DLL：游戏只扫包根与 Assemblies/，覆盖不到就算不上违规。
    let declared: BTreeSet<String> = manifest
        .assemblies
        .iter()
        .map(|v| v.replace('\\', "/").to_ascii_lowercase())
        .collect();
    for folder in [dir.to_path_buf(), dir.join("Assemblies")] {
        let Ok(entries) = fs::read_dir(&folder) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if !path.is_file() {
                continue;
            }
            let is_dll = path
                .extension()
                .map(|e| e.eq_ignore_ascii_case("dll"))
                .unwrap_or(false);
            if !is_dll {
                continue;
            }
            let name = path
                .file_name()
                .unwrap_or_default()
                .to_string_lossy()
                .to_string();
            let relative = if folder == dir {
                name.clone()
            } else {
                format!("Assemblies/{name}")
            };
            if !declared.contains(&relative.to_ascii_lowercase()) {
                issues.push(Issue {
                    level: Level::Error,
                    element: relative.clone(),
                    message: "包内有未列入 <Assemblies> 的 DLL，游戏会注册后拒绝启用。".into(),
                    fix: "写进 <Assemblies> 或把它移出包。".into(),
                    line: 0,
                });
            }
        }
    }

    ManifestReport {
        manifest,
        issues,
        package_root: dir.to_string_lossy().to_string(),
    }
}

/// 只解析不校验，用于快速读取 ID/名称。
pub fn read_manifest(dir: &Path) -> Option<Manifest> {
    let report = validate_package(dir);
    if report.manifest.id.is_empty() {
        None
    } else {
        Some(report.manifest)
    }
}

pub fn package_root_for(entry: &Path) -> PathBuf {
    entry.to_path_buf()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn mk(dir: &Path, xml: &str, dll: Option<&[u8]>) {
        fs::create_dir_all(dir).unwrap();
        let mut f = fs::File::create(dir.join("manifest.xml")).unwrap();
        f.write_all(xml.as_bytes()).unwrap();
        if let Some(bytes) = dll {
            fs::write(dir.join("Test.dll"), bytes).unwrap();
        }
    }

    fn temp(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("ps-manifest-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        dir
    }

    #[test]
    fn accepts_valid_manifest() {
        let dir = temp("ok");
        mk(
            &dir,
            r#"<?xml version="1.0"?><Manifest><ID>my_mod</ID><Name>N</Name><Author>A</Author><ModVersion>1.0.0</ModVersion><Description>D</Description><Assemblies><Assembly>Test.dll</Assembly></Assemblies><Capabilities /></Manifest>"#,
            Some(b"fake IMod payload"),
        );
        let report = validate_package(&dir);
        assert!(!report.has_errors(), "{:?}", report.issues);
        assert_eq!(report.manifest.id, "my_mod");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn rejects_bad_id_and_missing_capabilities() {
        let dir = temp("badid");
        mk(
            &dir,
            r#"<Manifest><ID>bad id</ID><Assemblies><Assembly>Test.dll</Assembly></Assemblies></Manifest>"#,
            Some(b"IMod"),
        );
        let report = validate_package(&dir);
        assert!(report.has_errors());
        assert!(report.issues.iter().any(|i| i.element == "<ID>"));
        assert!(report.issues.iter().any(|i| i.element == "<Capabilities>"));
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn reads_capability_text_not_element_name() {
        let dir = temp("caps");
        mk(
            &dir,
            r#"<Manifest><ID>a</ID><Assemblies><Assembly>Test.dll</Assembly></Assemblies><Capabilities><Capability>files</Capability><Capability>steam</Capability></Capabilities></Manifest>"#,
            Some(b"IMod"),
        );
        let report = validate_package(&dir);
        assert!(!report.has_errors(), "{:?}", report.issues);
        assert_eq!(report.manifest.capabilities, vec!["files", "steam"]);
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn flags_undeclared_dll() {
        let dir = temp("undeclared");
        mk(
            &dir,
            r#"<Manifest><ID>a</ID><Assemblies><Assembly>Test.dll</Assembly></Assemblies><Capabilities /></Manifest>"#,
            Some(b"IMod"),
        );
        fs::write(dir.join("Extra.dll"), b"x").unwrap();
        let report = validate_package(&dir);
        assert!(report.issues.iter().any(|i| i.element == "Extra.dll"));
        let _ = fs::remove_dir_all(&dir);
    }
}
