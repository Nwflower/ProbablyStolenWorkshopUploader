import { useEffect, useMemo, useRef, useState } from "react";
import {
  AlertTriangle,
  Check,
  CirclePlay,
  Info,
  Languages,
  Loader2,
  RefreshCw,
  Search,
  Tags,
  Upload,
} from "lucide-react";
import { api, browserShim } from "./lib/ipc";
import {
  buildPlan,
  emptyDraft,
  matchPublishedId,
  parseTagInput,
  summarize,
  targetLabel,
  toggleTag,
} from "./lib/plan";
import type {
  Draft,
  ItemSummary,
  LocalMod,
  ModsView,
  PublishPlan,
  SteamEvent,
  SteamStatus,
  TaskSnapshot,
  TaskStatus,
} from "./lib/types";
import { LANGUAGES, SUGGESTED_TAGS, VISIBILITY } from "./lib/types";
import "./App.css";

const STATUS_LABEL: Record<TaskStatus, string> = {
  running: "进行中",
  succeeded: "已完成",
  failed: "失败",
  unconfirmed: "结果未确认",
};

export default function App() {
  const [view, setView] = useState<ModsView | null>(null);
  const [steam, setSteam] = useState<SteamStatus | null>(null);
  const [items, setItems] = useState<ItemSummary[]>([]);
  const [querying, setQuerying] = useState(false);
  const [error, setError] = useState("");
  const [query, setQuery] = useState("");
  const [selectedId, setSelectedId] = useState("");
  const [drafts, setDrafts] = useState<Record<string, Draft>>({});
  const [task, setTask] = useState<TaskSnapshot | null>(null);
  const [logOpen, setLogOpen] = useState(false);
  const touchedTargets = useRef(new Set<string>());
  const autoQueryStarted = useRef(false);

  useEffect(() => {
    let dispose: (() => void) | undefined;

    function applyEvent(event: SteamEvent) {
      switch (event.kind) {
        case "status":
          setSteam(event.status);
          if (!event.status.busy) setQuerying(false);
          break;
        case "items":
          setItems((old) => {
            const merged = new Map(old.map((item) => [item.id, item]));
            for (const item of event.items) merged.set(item.id, item);
            return [...merged.values()];
          });
          setQuerying(false);
          break;
        case "task":
          setTask(event.task);
          break;
        case "published":
          if (api.isTauri) void api.rememberPublished(event.modId, event.id);
          touchedTargets.current.add(event.modId);
          setDrafts((all) => {
            const current = all[event.modId];
            if (!current) return all;
            return {
              ...all,
              [event.modId]: { ...current, target: { kind: "existing", id: event.id } },
            };
          });
          setView((current) =>
            current
              ? {
                  ...current,
                  mods: current.mods.map((item) =>
                    item.id === event.modId ? { ...item, publishedId: event.id } : item,
                  ),
                }
              : current,
          );
          break;
        case "log":
          setError((old) => old || event.line);
          break;
      }
    }

    (async () => {
      try {
        if (api.isTauri) {
          const mods = await api.listMods();
          setView(mods);
          setSelectedId(mods.mods[0]?.id ?? "");
          const initial: Record<string, Draft> = {};
          for (const item of mods.mods) initial[item.id] = emptyDraft(item);
          setDrafts(initial);
          if (mods.error) setError(mods.error);
          setSteam(await api.steamStatus());
          dispose = await api.listen((event) => applyEvent(event));
        } else {
          const shim = await browserShim();
          setView(shim.mods);
          setSelectedId(shim.mods.mods[0]?.id ?? "");
          const initial: Record<string, Draft> = {};
          for (const item of shim.mods.mods) initial[item.id] = emptyDraft(item);
          setDrafts(initial);
          setSteam(shim.status);
        }
      } catch (reason) {
        setError(String(reason));
      }
    })();

    return () => dispose?.();
  }, []);

  function refreshTargets() {
    if (!api.isTauri || !steam?.connected) return;
    setQuerying(true);
    setError("");
    api.queryItems("").catch((reason) => {
      setQuerying(false);
      setError(String(reason));
    });
  }

  useEffect(() => {
    if (steam?.connected && !autoQueryStarted.current) {
      autoQueryStarted.current = true;
      refreshTargets();
    } else if (!steam?.connected) {
      autoQueryStarted.current = false;
    }
  }, [steam?.connected]);

  // 查询到本账号物品后自动关联；人工改过目标的模组不被后台结果覆盖。
  useEffect(() => {
    if (!view || items.length === 0) return;
    setDrafts((all) => {
      let changed = false;
      const next = { ...all };
      for (const item of view.mods) {
        const current = next[item.id];
        if (!current || current.target.kind !== "new" || touchedTargets.current.has(item.id)) {
          continue;
        }
        const matched = matchPublishedId(item, items);
        if (!matched) continue;
        next[item.id] = {
          ...current,
          target: { kind: "existing", id: matched },
          keepTags: true,
          keepDescription: true,
          visibility: null,
        };
        changed = true;
      }
      return changed ? next : all;
    });
  }, [items, view]);

  function refreshMods() {
    if (!api.isTauri) return;
    api
      .listMods()
      .then((mods) => {
        setView(mods);
        setError(mods.error);
        setDrafts((all) => {
          const next = { ...all };
          for (const item of mods.mods) {
            if (!next[item.id]) next[item.id] = emptyDraft(item);
          }
          return next;
        });
      })
      .catch((reason) => setError(String(reason)));
  }

  const mod: LocalMod | undefined = view?.mods.find((m) => m.id === selectedId);
  const draft = selectedId ? drafts[selectedId] : undefined;
  const plan = useMemo(() => (mod && draft ? buildPlan(mod, draft) : null), [mod, draft]);

  const mods = useMemo(() => {
    if (!view) return [];
    const needle = query.trim().toLowerCase();
    if (!needle) return view.mods;
    return view.mods.filter(
      (m) =>
        m.name.toLowerCase().includes(needle) ||
        m.id.toLowerCase().includes(needle) ||
        m.path.toLowerCase().includes(needle),
    );
  }, [view, query]);

  function patchDraft(patch: Partial<Draft>) {
    if (!draft || !selectedId) return;
    setDrafts((all) => ({ ...all, [selectedId]: { ...draft, ...patch } }));
  }

  function markTargetTouched() {
    if (selectedId) touchedTargets.current.add(selectedId);
  }

  function patchText(language: string, patch: { title?: string; description?: string }) {
    if (!draft) return;
    const current = draft.texts[language];
    patchDraft({ texts: { ...draft.texts, [language]: { ...current, ...patch } } });
  }

  function showReview() {
    document.getElementById("review")?.scrollIntoView({ block: "start" });
  }

  function blockers(value: PublishPlan): string[] {
    const list: string[] = [];
    if (!mod) list.push("未选择模组。");
    else if (mod.errors > 0) list.push(`清单有 ${mod.errors} 个错误，修复后才能发布。`);
    if (!steam?.connected) list.push("Steam 未连接，无法发布。");
    if (value.languages.length === 0) list.push("至少填写一种语言的标题。");
    if (!value.languages.some((l) => l.key === "english")) {
      list.push("英文必须填写，作为首个提交语言。");
    }
    if (value.mode === "full" && !value.contentFolder) {
      list.push("完整发布需要内容目录。");
    }
    if (value.target.kind === "existing" && !/^\d+$/.test(value.target.id)) {
      list.push("工坊 ID 必须是十进制数字。");
    }
    if (!value.keepTags && value.tags.length > 20) {
      list.push("TAG 最多 20 个。");
    }
    return list;
  }

  function startPublish() {
    if (!plan || !mod) return;
    const problems = blockers(plan);
    if (problems.length > 0) {
      showReview();
      setError(problems.join("；"));
      return;
    }
    setError("");
    setLogOpen(true);
    if (api.isTauri) {
      api.publish(plan).catch((reason) => setError(String(reason)));
    } else {
      setTask({
        id: "browser",
        status: "failed",
        progress: 0,
        round: 1,
        totalRounds: 1,
        language: "english",
        message: "浏览器预览不会发布。",
        confirmedLanguages: [],
        logs: ["浏览器预览不连接 Steam。"],
      });
    }
  }

  if (!view || !mod || !draft || !plan) {
    return (
      <main className="boot">
        <Loader2 className="spin" size={22} />
        <p>{error || "正在读取本地模组与 Steam 状态…"}</p>
      </main>
    );
  }

  const activeText = draft.texts[draft.activeLanguage];
  const existingId = draft.target.kind === "existing" ? draft.target.id : "";
  const selectedItem = items.find((item) => item.id === existingId);
  const preview = view.previews[mod.id] ?? "";
  const filled = LANGUAGES.filter((l) => (draft.texts[l.key]?.title ?? "").trim().length > 0);
  const running = task?.status === "running" || steam?.busy === true;
  const manifestIssues = mod.issues;
  const planIssues = blockers(plan);

  return (
    <div className="app">
      <header className="titlebar">
        <div className="brand">
          <span className="mark">PS</span>
          <div>
            <strong>创意工坊上传器</strong>
            <span className="brandSub">{view.game ? view.game.gameDir : "未找到游戏目录"}</span>
          </div>
        </div>
        <div className="titlebarRight">
          <span className={`modeBadge ${steam?.connected ? "ok" : "warn"}`}>
            {steam?.connected ? `Steam · ${steam.user}` : steam?.message || "正在连接 Steam"}
          </span>
          <button
            type="button"
            className="ghost small"
            onClick={refreshMods}
            disabled={!api.isTauri}
          >
            <RefreshCw size={13} /> 重新扫描
          </button>
        </div>
      </header>

      <div className="body">
        <aside className="sidebar">
          <label className="search">
            <Search size={15} />
            <input
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder="搜索模组、ID 或路径"
              aria-label="搜索模组"
            />
          </label>
          <div className="sidebarMeta">
            <span>{mods.length} 个模组</span>
            <span className="muted">{view.game ? "仅本地 Mods 目录" : "未找到目录"}</span>
          </div>
          <ul className="modList">
            {mods.map((item) => (
              <li key={item.id}>
                <button
                  type="button"
                  className={`modRow ${item.id === selectedId ? "active" : ""}`}
                  onClick={() => {
                    setSelectedId(item.id);
                    setError("");
                  }}
                >
                  <span className="avatar blue">
                    {view.previews[item.id] ? (
                      <img src={view.previews[item.id]} alt="" />
                    ) : (
                      item.name.slice(0, 1)
                    )}
                  </span>
                  <span className="modTextBox">
                    <span className="modName">{item.name}</span>
                    <span className="modSub">
                      {item.author || "未知作者"}
                      {item.capabilities.length > 0 ? ` · ${item.capabilities.join("/")}` : ""}
                    </span>
                    <span className="modPath">{item.folder}</span>
                  </span>
                  <span className="modSide">
                    <span className="pill">v{item.version || "—"}</span>
                    {item.errors > 0 && <span className="badgeError">{item.errors} 错误</span>}
                  </span>
                </button>
              </li>
            ))}
            {mods.length === 0 && (
              <li className="empty">
                {view.error || "这个目录下没有带 manifest.xml 的模组"}
              </li>
            )}
          </ul>
        </aside>

        <main className="main">
          <div className="mainHead">
            <div>
              <h1>{mod.name}</h1>
              <p className="modPathWide">{mod.path}</p>
            </div>
            <span className={`badge ${mod.errors > 0 ? "bad" : "good"}`}>
              {mod.errors > 0 ? `清单 ${mod.errors} 错误` : "清单通过"}
            </span>
          </div>

          {error && (
            <div className="banner error">
              <AlertTriangle size={15} /> {error}
            </div>
          )}

          <div className="panelScroll">
            <div className="singlePage">
              <div className="stack">
                <section className="card">
                  <h2>发布目标</h2>
                  <div className="segmented">
                    <button
                      type="button"
                      className={draft.target.kind === "existing" ? "seg active" : "seg"}
                      onClick={() => {
                        markTargetTouched();
                        patchDraft({
                          target: {
                            kind: "existing",
                            id:
                              draft.target.kind === "existing"
                                ? draft.target.id
                                : mod.publishedId || items[0]?.id || "",
                          },
                          keepTags: true,
                          keepDescription: true,
                          visibility: null,
                        });
                      }}
                    >
                      已有物品
                    </button>
                    <button
                      type="button"
                      className={draft.target.kind === "new" ? "seg active" : "seg"}
                      onClick={() => {
                        markTargetTouched();
                        patchDraft({
                          target: { kind: "new" },
                          keepTags: false,
                          keepDescription: false,
                          visibility: 2,
                        });
                      }}
                    >
                      新建物品
                    </button>
                  </div>

                  {draft.target.kind === "existing" ? (
                    <>
                      <label className="field">
                        <span>要更新的工坊物品</span>
                        <select
                          value={existingId}
                          onChange={(event) => {
                            markTargetTouched();
                            patchDraft({
                              target: { kind: "existing", id: event.target.value },
                            });
                          }}
                        >
                          {existingId && !selectedItem && (
                            <option value={existingId}>本地记忆 · {existingId}</option>
                          )}
                          {!existingId && <option value="">请选择物品</option>}
                          {items.map((item) => (
                            <option key={item.id} value={item.id}>
                              {item.title || "(无标题)"} · {item.id}
                            </option>
                          ))}
                        </select>
                        <small>
                          {querying
                            ? "正在读取本账号物品并自动匹配…"
                            : selectedItem
                              ? `已匹配 · ${VISIBILITY[selectedItem.visibility]?.label ?? selectedItem.visibility}`
                              : "已使用本地记忆；可刷新列表核对。"}
                        </small>
                      </label>
                      <button
                        type="button"
                        className="ghost small"
                        onClick={refreshTargets}
                        disabled={!api.isTauri || !steam?.connected || querying}
                      >
                        <RefreshCw size={13} className={querying ? "spin" : ""} /> 刷新物品列表
                      </button>
                      <details className="advancedId">
                        <summary>手动输入 / 核对 ID</summary>
                        <label className="field">
                          <span>工坊物品 ID</span>
                          <input
                            value={existingId}
                            inputMode="numeric"
                            onChange={(event) => {
                              markTargetTouched();
                              patchDraft({
                                target: { kind: "existing", id: event.target.value.trim() },
                              });
                            }}
                          />
                        </label>
                        <button
                          type="button"
                          className="ghost small"
                          onClick={() => {
                            setQuerying(true);
                            api.queryItems(existingId).catch((reason) => {
                              setQuerying(false);
                              setError(String(reason));
                            });
                          }}
                          disabled={!api.isTauri || !steam?.connected || !existingId}
                        >
                          核对该 ID
                        </button>
                      </details>
                    </>
                  ) : (
                    <p className="hint">
                      <Info size={14} /> 新建取得 ID 后会自动记录，下次无需手填。
                    </p>
                  )}

                  <h2>发布模式</h2>
                  <div className="segmented">
                    <button
                      type="button"
                      className={draft.mode === "full" ? "seg active" : "seg"}
                      onClick={() => patchDraft({ mode: "full" })}
                    >
                      完整发布
                    </button>
                    <button
                      type="button"
                      className={draft.mode === "page" ? "seg active" : "seg"}
                      disabled={draft.target.kind === "new"}
                      onClick={() => patchDraft({ mode: "page" })}
                    >
                      仅更新页面
                    </button>
                  </div>
                  <p className="hint">
                    {draft.mode === "full"
                      ? `首轮会上传内容目录：${mod.packageRoot}`
                      : "不设置内容目录，只更新页面字段。"}
                  </p>
                </section>

                <section className="card">
                  <h2>可见性与更新说明</h2>
                  <label className="field">
                    <span>可见性</span>
                    <select
                      value={draft.visibility ?? ""}
                      onChange={(event) =>
                        patchDraft({
                          visibility:
                            event.target.value === "" ? null : Number(event.target.value),
                        })
                      }
                    >
                      <option value="">保留工坊现有</option>
                      {VISIBILITY.map((item) => (
                        <option key={item.value} value={item.value}>
                          {item.label}
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="field">
                    <span>更新说明</span>
                    <textarea
                      rows={4}
                      value={draft.changeNote}
                      onChange={(event) => patchDraft({ changeNote: event.target.value })}
                      placeholder="本次更新说明，换行会原样提交"
                    />
                  </label>
                </section>

                <section className="card">
                  <h2>
                    <Tags size={16} /> 工坊 TAG
                  </h2>
                  <div className="tagBox">
                    {draft.tags.length === 0 && <span className="muted">尚未添加 TAG</span>}
                    {draft.tags.map((tag) => (
                      <span key={tag} className="tagChip">
                        {tag}
                        <button
                          type="button"
                          aria-label={`移除 ${tag}`}
                          disabled={draft.keepTags}
                          onClick={() => patchDraft({ tags: toggleTag(draft.tags, tag) })}
                        >
                          ×
                        </button>
                      </span>
                    ))}
                  </div>
                  <label className="field">
                    <span>添加 TAG</span>
                    <input
                      disabled={draft.keepTags}
                      placeholder="输入后按回车，可用逗号分隔"
                      onKeyDown={(event) => {
                        if (event.key !== "Enter") return;
                        event.preventDefault();
                        const added = parseTagInput(event.currentTarget.value);
                        if (added.length === 0) return;
                        patchDraft({ tags: added.reduce(toggleTag, draft.tags) });
                        event.currentTarget.value = "";
                      }}
                    />
                  </label>
                  <div className="suggest">
                    {SUGGESTED_TAGS.map((tag) => (
                      <button
                        type="button"
                        key={tag}
                        className="suggestChip"
                        disabled={draft.keepTags}
                        onClick={() => patchDraft({ tags: toggleTag(draft.tags, tag) })}
                      >
                        {tag}
                      </button>
                    ))}
                  </div>
                  <label className="check">
                    <input
                      type="checkbox"
                      checked={draft.keepTags}
                      disabled={draft.target.kind === "new"}
                      onChange={(event) => patchDraft({ keepTags: event.target.checked })}
                    />
                    更新时保留工坊现有 TAG
                  </label>
                </section>
              </div>

              <div className="stack">
                <section className="card">
                  <h2>模组预览图</h2>
                  {preview ? (
                    <img className="previewImage" src={preview} alt={`${mod.name} 预览图`} />
                  ) : (
                    <div className="previewEmpty">
                      将 preview.png、preview.jpg 或 preview.jpeg 放在模组根目录后会自动显示。
                    </div>
                  )}
                  <p className="hint">发布时使用本地记忆的预览路径；没有记忆时使用上述模组预览图。</p>
                </section>

                <section className="card">
                  <h2>
                    <Languages size={16} /> 页面文案
                  </h2>
                  <div className="langGrid">
                    {LANGUAGES.map((language) => {
                      const text = draft.texts[language.key];
                      const done = (text?.title ?? "").trim().length > 0;
                      return (
                        <button
                          type="button"
                          key={language.key}
                          className={`langChip ${
                            draft.activeLanguage === language.key ? "active" : ""
                          } ${done ? "done" : ""}`}
                          onClick={() => patchDraft({ activeLanguage: language.key })}
                        >
                          {done && <Check size={12} />}
                          {language.label}
                        </button>
                      );
                    })}
                  </div>
                  <label className="field">
                    <span>标题 · {activeText.key}</span>
                    <input
                      value={activeText.title}
                      onChange={(event) => patchText(activeText.key, { title: event.target.value })}
                      placeholder="工坊页面标题"
                    />
                  </label>
                  <label className="field">
                    <span>描述</span>
                    <textarea
                      rows={6}
                      value={activeText.description}
                      onChange={(event) =>
                        patchText(activeText.key, { description: event.target.value })
                      }
                      placeholder="工坊页面描述"
                    />
                  </label>
                  <label className="check">
                    <input
                      type="checkbox"
                      checked={draft.keepDescription}
                      disabled={draft.target.kind === "new"}
                      onChange={(event) => patchDraft({ keepDescription: event.target.checked })}
                    />
                    更新时保留工坊现有描述
                  </label>
                </section>

                <section className="card reviewCard" id="review">
                  <h2>发布检查</h2>
                  <dl className="summary">
                    {summarize(plan).map((row) => (
                      <div key={row.label}>
                        <dt>{row.label}</dt>
                        <dd>{row.value}</dd>
                      </div>
                    ))}
                  </dl>
                  <div className="reviewResult">
                    {planIssues.length === 0 ? (
                      <p className="okText">
                        <Check size={15} /> 可以提交。
                      </p>
                    ) : (
                      <ul className="issues">
                        {planIssues.map((item, index) => (
                          <li key={`${item}-${index}`}>
                            <AlertTriangle size={14} />
                            <span>{item}</span>
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>
                  {manifestIssues.length > 0 && (
                    <ul className="issues manifestIssues">
                      {manifestIssues.map((issue, index) => (
                        <li key={`${issue.element}-${index}`} className={issue.level}>
                          <AlertTriangle size={14} />
                          <span className="issueField">
                            {issue.element}
                            {issue.line > 0 ? `:${issue.line}` : ""}
                          </span>
                          <span>
                            {issue.message}
                            <br />
                            <small>{issue.fix}</small>
                          </span>
                        </li>
                      ))}
                    </ul>
                  )}
                </section>

                {task && (
                  <section className="card">
                    <h2>发布状态</h2>
                    <div className="progressRow">
                      <div className="progress">
                        <span style={{ width: `${task.progress}%` }} />
                      </div>
                      <span className="progressText">{task.progress}%</span>
                    </div>
                    <p className={`statusLine ${task.status}`}>
                      {task.status === "running" && <Loader2 className="spin" size={14} />}
                      {task.status === "succeeded" && <Check size={14} />}
                      {task.status !== "running" && task.status !== "succeeded" && (
                        <AlertTriangle size={14} />
                      )}
                      {STATUS_LABEL[task.status]} · 第 {task.round}/{task.totalRounds} 轮 ·{" "}
                      {task.language}
                    </p>
                    <p className="hint">{task.message}</p>
                    {task.confirmedLanguages.length > 0 && (
                      <p className="muted">已确认语言：{task.confirmedLanguages.join("、")}</p>
                    )}
                  </section>
                )}
              </div>
            </div>
          </div>
        </main>
      </div>

      <footer className="actionbar">
        <div className="actionInfo">
          <span className="actionTarget">{targetLabel(plan)}</span>
          <span className="muted">
            {draft.mode === "full" ? "完整发布" : "仅更新页面"} · 已填写 {filled.length} 种语言
          </span>
          <span className={`statusDot ${task?.status ?? "idle"}`}>
            {task ? STATUS_LABEL[task.status] : steam?.connected ? "待命" : "Steam 未连接"}
          </span>
        </div>
        <div className="actionButtons">
          <button type="button" className="ghost" onClick={showReview} disabled={running}>
            <Check size={14} /> 发布检查
          </button>
          <button
            type="button"
            className="primary"
            onClick={startPublish}
            disabled={running || !steam?.connected || mod.errors > 0}
          >
            {running ? <Loader2 className="spin" size={15} /> : <CirclePlay size={15} />}
            开始发布
          </button>
        </div>
      </footer>

      <section className={`logPanel ${logOpen ? "open" : ""}`}>
        <button type="button" className="logToggle" onClick={() => setLogOpen((value) => !value)}>
          <Upload size={13} /> 日志 {task ? `· ${task.logs.length} 条` : "· 空"}
          <span className="muted">{logOpen ? "收起" : "展开"}</span>
        </button>
        {logOpen && <pre className="logBody">{(task?.logs ?? ["尚无任务。"]).join("\n")}</pre>}
      </section>
    </div>
  );
}
