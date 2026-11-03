//! Steam 适配层：单个专用线程持有 Client，串行处理查询与发布。
//!
//! 设计要点：
//! - `Client` 可以跨线程，但 `UpdateHandle` 不能；所以所有 Steam 调用都在本线程完成。
//! - 回调通过 `run_callbacks` 在本线程派发，结果经内部通道回收，再推进状态机。
//! - 本模块不主动发起任何发布；只有 UI 明确提交计划时才调用。

use std::path::PathBuf;
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};
use steamworks::{
    AppIDs, AppId, Client, PublishedFileId, PublishedFileVisibility, UGCQueryType, UGCType,
    UserList, UserListOrder,
};
use uploader_core::{PublishPlan, Round, Target, TaskSnapshot, TaskStatus};

pub const APP_ID: u32 = 4348910;

#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SteamStatus {
    pub connected: bool,
    pub user: String,
    pub app_id: u32,
    pub message: String,
    pub busy: bool,
}

#[derive(Debug, Clone, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ItemSummary {
    pub id: String,
    pub title: String,
    pub visibility: u8,
    pub tags: Vec<String>,
}

#[derive(Debug, Clone, serde::Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum Event {
    Status {
        status: SteamStatus,
    },
    Log {
        line: String,
    },
    Items {
        items: Vec<ItemSummary>,
        truncated: bool,
    },
    Task {
        task: TaskSnapshot,
    },
    #[serde(rename_all = "camelCase")]
    Published {
        mod_id: String,
        id: String,
    },
}

pub enum Command {
    Query(String),
    Publish(Box<PublishPlan>),
    Shutdown,
}

enum Internal {
    Created(Result<(u64, bool), String>),
    Submitted(Result<(u64, bool), String>),
    Page { items: Vec<ItemSummary> },
    Failed(String),
}

pub struct SteamWorker {
    commands: Sender<Command>,
    shared: Arc<Mutex<Option<SteamStatus>>>,
}

impl SteamWorker {
    pub fn spawn<F>(emit: F) -> Self
    where
        F: Fn(Event) + Send + 'static,
    {
        let (cmd_tx, cmd_rx) = mpsc::channel::<Command>();
        let shared: Arc<Mutex<Option<SteamStatus>>> = Arc::new(Mutex::new(None));
        let shared_for_thread = Arc::clone(&shared);
        thread::Builder::new()
            .name("steam-worker".into())
            .spawn(move || run_worker(cmd_rx, emit, shared_for_thread))
            .expect("spawn steam worker");
        Self {
            commands: cmd_tx,
            shared,
        }
    }

    pub fn status(&self) -> Option<SteamStatus> {
        self.shared.lock().ok().and_then(|v| v.clone())
    }

    pub fn query(&self, id: String) {
        let _ = self.commands.send(Command::Query(id));
    }

    pub fn publish(&self, plan: PublishPlan) {
        let _ = self.commands.send(Command::Publish(Box::new(plan)));
    }
}

impl Drop for SteamWorker {
    fn drop(&mut self) {
        let _ = self.commands.send(Command::Shutdown);
    }
}

struct Job {
    plan: PublishPlan,
    rounds: Vec<Round>,
    index: usize,
    published_id: u64,
    awaiting: bool,
    watch: Option<steamworks::UpdateWatchHandle>,
    confirmed: Vec<String>,
    last_progress_at: Instant,
    last_progress: (u64, u64),
    logs: Vec<String>,
    pending_first_publish: bool,
    unconfirmed: bool,
}

impl Job {
    fn snapshot(&self, status: TaskStatus, message: impl Into<String>) -> TaskSnapshot {
        let total = self.rounds.len().max(1);
        let progress = if status == TaskStatus::Succeeded {
            100
        } else {
            let done = (self.index as u64) * 100 / total as u64;
            done.min(99) as u8
        };
        TaskSnapshot {
            id: format!("publish-{}", self.published_id),
            status,
            progress,
            round: (self.index + 1).min(total),
            total_rounds: total,
            language: self
                .rounds
                .get(self.index)
                .map(|r| r.language.clone())
                .unwrap_or_default(),
            message: message.into(),
            confirmed_languages: self.confirmed.clone(),
            logs: self.logs.clone(),
        }
    }
}

fn run_worker<F>(commands: Receiver<Command>, emit: F, shared: Arc<Mutex<Option<SteamStatus>>>)
where
    F: Fn(Event),
{
    let mut status = SteamStatus {
        connected: false,
        user: String::new(),
        app_id: APP_ID,
        message: "正在连接 Steam…".into(),
        busy: false,
    };

    let client = match Client::init_app(APP_ID) {
        Ok(client) => {
            for _ in 0..20 {
                client.run_callbacks();
                thread::sleep(Duration::from_millis(50));
            }
            status.connected = client.user().logged_on();
            status.user = client.friends().name();
            status.message = if status.connected {
                "已连接 Steam".into()
            } else {
                "Steam 已初始化，但账号尚未登录。".into()
            };
            Some(client)
        }
        Err(err) => {
            status.message = format!("Steam 初始化失败：{err}");
            None
        }
    };

    publish_status(&emit, &shared, &status);

    let Some(client) = client else {
        while let Ok(cmd) = commands.recv() {
            match cmd {
                Command::Shutdown => break,
                Command::Query(_) => emit(Event::Log {
                    line: "未连接 Steam，无法核对工坊物品。".into(),
                }),
                Command::Publish(_) => emit(Event::Log {
                    line: "未连接 Steam，已拒绝发布。".into(),
                }),
            }
        }
        return;
    };

    let (internal_tx, internal_rx) = mpsc::channel::<Internal>();
    let mut job: Option<Job> = None;

    loop {
        while let Ok(msg) = internal_rx.try_recv() {
            match msg {
                Internal::Created(result) => match result {
                    Ok((id, needs_eula)) => {
                        if let Some(active) = job.as_mut() {
                            active.published_id = id;
                            active.pending_first_publish = false;
                            active.logs.push(format!("已创建工坊物品 {id}"));
                            emit(Event::Published {
                                mod_id: active.plan.mod_id.clone(),
                                id: id.to_string(),
                            });
                            if needs_eula {
                                active.logs.push(
                                    "该账号尚未接受创意工坊法律协议，需先在网页同意一次。".into(),
                                );
                            }
                            if let Err(message) = begin_round(&client, active, &internal_tx, &emit)
                            {
                                active.logs.push(message.clone());
                                let snapshot = active.snapshot(TaskStatus::Failed, message);
                                emit(Event::Task { task: snapshot });
                                job = None;
                                status.busy = false;
                                publish_status(&emit, &shared, &status);
                            }
                        }
                    }
                    Err(message) => {
                        if let Some(active) = job.as_mut() {
                            active.awaiting = false;
                            let snapshot = active.snapshot(TaskStatus::Failed, message.clone());
                            emit(Event::Task { task: snapshot });
                            active.logs.push(message);
                        }
                        job = None;
                        status.busy = false;
                        publish_status(&emit, &shared, &status);
                    }
                },
                Internal::Submitted(result) => {
                    if let Some(active) = job.as_mut() {
                        active.awaiting = false;
                        match result {
                            Ok((id, needs_eula)) => {
                                active.published_id = id;
                                let language = active
                                    .rounds
                                    .get(active.index)
                                    .map(|r| r.language.clone())
                                    .unwrap_or_default();
                                active.confirmed.push(language.clone());
                                active.logs.push(format!("Steam 已确认 {language} 轮次。"));
                                if needs_eula {
                                    active.logs.push(
                                        "需要接受创意工坊法律协议后，物品才会对他人可见。".into(),
                                    );
                                }
                                active.index += 1;
                                active.watch = None;
                                if active.index >= active.rounds.len() {
                                    let snapshot = active
                                        .snapshot(TaskStatus::Succeeded, "全部语言轮次已确认。");
                                    emit(Event::Task { task: snapshot });
                                    job = None;
                                    status.busy = false;
                                    publish_status(&emit, &shared, &status);
                                } else if let Err(message) =
                                    begin_round(&client, active, &internal_tx, &emit)
                                {
                                    active.logs.push(message.clone());
                                    let snapshot = active.snapshot(TaskStatus::Failed, message);
                                    emit(Event::Task { task: snapshot });
                                    job = None;
                                    status.busy = false;
                                    publish_status(&emit, &shared, &status);
                                }
                            }
                            Err(message) => {
                                let snapshot = active.snapshot(TaskStatus::Failed, message.clone());
                                emit(Event::Task { task: snapshot });
                                active.logs.push(message);
                                job = None;
                                status.busy = false;
                                publish_status(&emit, &shared, &status);
                            }
                        }
                    }
                }
                Internal::Page { items } => {
                    emit(Event::Items {
                        items,
                        truncated: false,
                    });
                    status.busy = false;
                    publish_status(&emit, &shared, &status);
                }
                Internal::Failed(message) => {
                    emit(Event::Log { line: message });
                    if let Some(active) = job.as_mut() {
                        let snapshot = active.snapshot(TaskStatus::Failed, "查询失败。");
                        emit(Event::Task { task: snapshot });
                    }
                    job = None;
                    status.busy = false;
                    publish_status(&emit, &shared, &status);
                }
            }
        }

        match commands.recv_timeout(Duration::from_millis(40)) {
            Ok(Command::Shutdown) => break,
            Ok(Command::Query(id)) => {
                status.busy = true;
                publish_status(&emit, &shared, &status);
                let sender = internal_tx.clone();
                if id.trim().is_empty() {
                    // 空 ID 表示只读查询本账号物品，供界面自动匹配发布目标。
                    let handle = client
                        .ugc()
                        .query_user(
                            client.user().steam_id().account_id(),
                            UserList::Published,
                            UGCType::Items,
                            UserListOrder::LastUpdatedDesc,
                            app_ids(),
                            0,
                        )
                        .or_else(|_| {
                            client.ugc().query_all(
                                UGCQueryType::RankedByLastUpdatedDate,
                                UGCType::Items,
                                app_ids(),
                                0,
                            )
                        });
                    match handle {
                        Ok(handle) => handle.fetch(move |result| match result {
                            Ok(results) => {
                                let items = results
                                    .iter()
                                    .flatten()
                                    .map(|item| ItemSummary {
                                        id: item.published_file_id.0.to_string(),
                                        title: item.title,
                                        visibility: visibility_code(item.visibility),
                                        tags: item.tags,
                                    })
                                    .collect::<Vec<_>>();
                                let _ = sender.send(Internal::Page { items });
                            }
                            Err(err) => {
                                let _ = sender.send(Internal::Failed(format!("查询失败：{err}")));
                            }
                        }),
                        Err(_) => {
                            let _ = sender.send(Internal::Failed(
                                "无法创建物品列表查询，请确认 Steam 已登录。".into(),
                            ));
                        }
                    }
                } else {
                    match id.trim().parse::<u64>().ok().filter(|v| *v > 0) {
                        Some(file_id) => {
                            match client.ugc().query_items(vec![PublishedFileId(file_id)]) {
                                Ok(handle) => {
                                    handle.fetch(move |result| match result {
                                        Ok(results) => {
                                            let items = results
                                                .iter()
                                                .flatten()
                                                .map(|item| ItemSummary {
                                                    id: item.published_file_id.0.to_string(),
                                                    title: item.title,
                                                    visibility: visibility_code(item.visibility),
                                                    tags: item.tags,
                                                })
                                                .collect::<Vec<_>>();
                                            let _ = sender.send(Internal::Page { items });
                                        }
                                        Err(err) => {
                                            let _ = sender
                                                .send(Internal::Failed(format!("核对失败：{err}")));
                                        }
                                    });
                                }
                                Err(_) => {
                                    let _ = sender.send(Internal::Failed(
                                        "无法创建核对请求，请确认 Steam 已登录。".into(),
                                    ));
                                }
                            }
                        }
                        None => {
                            let _ = sender
                                .send(Internal::Failed("请填写有效的工坊 ID 后再核对。".into()));
                        }
                    }
                }
            }
            Ok(Command::Publish(plan)) => {
                if job.is_some() {
                    emit(Event::Log {
                        line: "已有发布任务在进行，已忽略新的请求。".into(),
                    });
                } else {
                    match start_job(&client, *plan, &internal_tx, &emit) {
                        Ok(active) => {
                            status.busy = true;
                            publish_status(&emit, &shared, &status);
                            job = Some(active);
                        }
                        Err(message) => emit(Event::Log { line: message }),
                    }
                }
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
        }

        if let Some(active) = job.as_mut() {
            if let Some(watch) = active.watch.as_ref() {
                let (_state, done, total) = watch.progress();
                if done != active.last_progress.0 || total != active.last_progress.1 {
                    active.last_progress = (done, total);
                    active.last_progress_at = Instant::now();
                }
            }
            if active.awaiting && active.last_progress_at.elapsed() > Duration::from_secs(120) {
                active.awaiting = false;
                active.unconfirmed = true;
                let snapshot = active.snapshot(
                    TaskStatus::Unconfirmed,
                    "等待超过两分钟仍无结果：这不代表失败，请先核对工坊页面。",
                );
                emit(Event::Task { task: snapshot });
                job = None;
                status.busy = false;
                publish_status(&emit, &shared, &status);
            } else {
                let message = if active.awaiting {
                    "等待 Steam 确认本轮提交…"
                } else {
                    "准备当前语言的页面字段…"
                };
                emit(Event::Task {
                    task: active.snapshot(TaskStatus::Running, message),
                });
            }
        }

        client.run_callbacks();
    }

    client.run_callbacks();
}

fn publish_status<F: Fn(Event)>(
    emit: &F,
    shared: &Arc<Mutex<Option<SteamStatus>>>,
    status: &SteamStatus,
) {
    if let Ok(mut slot) = shared.lock() {
        *slot = Some(status.clone());
    }
    emit(Event::Status {
        status: status.clone(),
    });
}

fn app_ids() -> AppIDs {
    AppIDs::Both {
        creator: AppId(APP_ID),
        consumer: AppId(APP_ID),
    }
}

fn visibility_code(value: PublishedFileVisibility) -> u8 {
    match value {
        PublishedFileVisibility::Public => 0,
        PublishedFileVisibility::FriendsOnly => 1,
        PublishedFileVisibility::Private => 2,
        PublishedFileVisibility::Unlisted => 3,
    }
}

fn visibility_from(code: u8) -> PublishedFileVisibility {
    match code {
        0 => PublishedFileVisibility::Public,
        1 => PublishedFileVisibility::FriendsOnly,
        3 => PublishedFileVisibility::Unlisted,
        _ => PublishedFileVisibility::Private,
    }
}

fn start_job<F: Fn(Event)>(
    client: &Client,
    plan: PublishPlan,
    internal: &Sender<Internal>,
    emit: &F,
) -> Result<Job, String> {
    let rounds = uploader_core::rounds(&plan).map_err(|issues| {
        issues
            .into_iter()
            .map(|i| i.message)
            .collect::<Vec<_>>()
            .join("\n")
    })?;
    if rounds.is_empty() {
        return Err("没有可提交的语言轮次。".into());
    }
    let mut job = Job {
        plan,
        rounds,
        index: 0,
        published_id: 0,
        awaiting: false,
        watch: None,
        confirmed: Vec::new(),
        last_progress_at: Instant::now(),
        last_progress: (0, 0),
        logs: vec!["发布计划已冻结。".into()],
        pending_first_publish: false,
        unconfirmed: false,
    };

    match job.plan.target.clone() {
        Target::Existing { id } => {
            let parsed: u64 = id.parse().map_err(|_| format!("工坊 ID 无效：{id}"))?;
            if parsed == 0 {
                return Err("工坊 ID 不能为 0；新建请选择新建物品。".into());
            }
            job.published_id = parsed;
            job.logs.push(format!("更新已有物品 {parsed}"));
            emit(Event::Task {
                task: job.snapshot(TaskStatus::Running, "准备更新已有物品…"),
            });
            begin_round(client, &mut job, internal, emit)?;
        }
        Target::New => {
            job.pending_first_publish = true;
            job.logs.push("新建工坊物品…".into());
            emit(Event::Task {
                task: job.snapshot(TaskStatus::Running, "正在创建工坊物品…"),
            });
            let sender = internal.clone();
            client.ugc().create_item(
                AppId(APP_ID),
                steamworks::FileType::Community,
                move |result| {
                    let _ = sender.send(Internal::Created(
                        result
                            .map(|(id, eula)| (id.0, eula))
                            .map_err(|e| format!("CreateItem 失败：{e}")),
                    ));
                },
            );
        }
    }
    Ok(job)
}

fn begin_round<F: Fn(Event)>(
    client: &Client,
    job: &mut Job,
    internal: &Sender<Internal>,
    emit: &F,
) -> Result<(), String> {
    let Some(round) = job.rounds.get(job.index).cloned() else {
        return Ok(());
    };
    let first = job.index == 0;
    let mut handle = client
        .ugc()
        .start_item_update(AppId(APP_ID), PublishedFileId(job.published_id));

    let text = job
        .plan
        .languages
        .iter()
        .find(|l| l.key == round.language)
        .cloned();

    handle = handle.language(&round.language);
    if let Some(text) = text.as_ref() {
        if !text.title.trim().is_empty() {
            handle = handle.title(text.title.trim());
        }
        if round.set_description && !text.description.trim().is_empty() {
            handle = handle.description(text.description.trim());
        }
    }

    if first {
        if round.upload_content {
            let folder = PathBuf::from(&job.plan.content_folder);
            if !folder.is_dir() {
                return Err(format!("内容目录不存在：{}", folder.display()));
            }
            handle = handle.content_path(&folder);
        }
        if !job.plan.preview_path.trim().is_empty() {
            let preview = PathBuf::from(&job.plan.preview_path);
            if !preview.is_file() {
                return Err(format!("预览图不存在：{}", preview.display()));
            }
            handle = handle.preview_path(&preview);
        }
        if let Some(visibility) = round.visibility {
            handle = handle.visibility(visibility_from(visibility));
        }
        if round.set_tags {
            handle = handle.tags(round.tags.clone(), false);
        }
    }

    let note = if job.plan.change_note.trim().is_empty() {
        None
    } else {
        Some(job.plan.change_note.clone())
    };
    let sender = internal.clone();
    let watch = handle.submit(note.as_deref(), move |result| {
        let _ = sender.send(Internal::Submitted(
            result
                .map(|(id, eula)| (id.0, eula))
                .map_err(|e| format!("提交失败：{e}")),
        ));
    });
    job.watch = Some(watch);
    job.awaiting = true;
    job.last_progress_at = Instant::now();
    job.logs.push(format!(
        "提交第 {}/{} 轮：{}{}",
        job.index + 1,
        job.rounds.len(),
        round.language,
        if first && round.upload_content {
            "（含内容）"
        } else {
            "（仅文案）"
        }
    ));
    emit(Event::Task {
        task: job.snapshot(TaskStatus::Running, "等待 Steam 确认本轮提交…"),
    });
    Ok(())
}

/// 只读自检：初始化 Steam、报告账号，不创建、不修改任何物品。
pub fn selftest() -> i32 {
    let mut lines: Vec<String> = Vec::new();
    let code;
    match Client::init_app(APP_ID) {
        Ok(client) => {
            for _ in 0..40 {
                client.run_callbacks();
                if client.user().logged_on() {
                    break;
                }
                thread::sleep(Duration::from_millis(100));
            }
            let connected = client.user().logged_on();
            lines.push(format!("connected={connected}"));
            lines.push(format!("user={}", client.friends().name()));
            lines.push(format!("appid={}", client.utils().app_id().0));
            code = if connected { 0 } else { 4 };
        }
        Err(err) => {
            lines.push(format!("INIT FAILED: {err}"));
            code = 2;
        }
    }
    let text = lines.join(
        "
",
    );
    println!("{text}");
    if let Ok(exe) = std::env::current_exe() {
        let log = exe.with_file_name("selftest.log");
        let _ = std::fs::write(log, &text);
    }
    code
}

/// 只读自检：查询当前账号已发布的物品，打印到 stdout 与 querytest.log。
pub fn querytest() -> i32 {
    let mut lines: Vec<String> = Vec::new();
    let code;
    match Client::init_app(APP_ID) {
        Ok(client) => {
            for _ in 0..40 {
                client.run_callbacks();
                if client.user().logged_on() {
                    break;
                }
                thread::sleep(Duration::from_millis(100));
            }
            if !client.user().logged_on() {
                lines.push("NOT LOGGED ON".into());
                code = 4;
            } else {
                let account = client.user().steam_id().account_id();
                lines.push(format!("user={}", client.friends().name()));
                let mut page = 0u32;
                loop {
                    let (tx, rx) = mpsc::channel();
                    let _ = account;
                    let handle = client
                        .ugc()
                        .query_user(
                            client.user().steam_id().account_id(),
                            UserList::Published,
                            UGCType::Items,
                            UserListOrder::LastUpdatedDesc,
                            app_ids(),
                            page,
                        )
                        .or_else(|_| {
                            client.ugc().query_all(
                                UGCQueryType::RankedByLastUpdatedDate,
                                UGCType::Items,
                                app_ids(),
                                page,
                            )
                        });
                    let Ok(handle) = handle else {
                        lines.push("CREATE QUERY FAILED".into());
                        break;
                    };
                    handle.fetch(move |result| {
                        let payload = match result {
                            Ok(results) => results
                                .iter()
                                .flatten()
                                .map(|item| {
                                    format!(
                                        "{} | {} | tags={}",
                                        item.published_file_id.0,
                                        item.title,
                                        item.tags.join(",")
                                    )
                                })
                                .collect::<Vec<_>>(),
                            Err(err) => vec![format!("QUERY ERROR: {err}")],
                        };
                        let _ = tx.send(payload);
                    });
                    let mut payload = None;
                    for _ in 0..600 {
                        client.run_callbacks();
                        if let Ok(value) = rx.try_recv() {
                            payload = Some(value);
                            break;
                        }
                        thread::sleep(Duration::from_millis(50));
                    }
                    match payload {
                        Some(items) => {
                            let empty = items.is_empty();
                            lines.extend(items);
                            if empty || page >= 9 {
                                break;
                            }
                            page += 1;
                        }
                        None => {
                            lines.push("QUERY TIMEOUT".into());
                            break;
                        }
                    }
                }
                code = 0;
            }
        }
        Err(err) => {
            lines.push(format!("INIT FAILED: {err}"));
            code = 2;
        }
    }
    let text = lines.join(
        "
",
    );
    println!("{text}");
    if let Ok(exe) = std::env::current_exe() {
        let _ = std::fs::write(exe.with_file_name("querytest.log"), &text);
    }
    code
}
