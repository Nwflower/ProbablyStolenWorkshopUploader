import type { Draft, ItemSummary, LocalMod, PublishPlan } from "./types";
import { LANGUAGES } from "./types";

/** Presentation-only helpers. Publish rules live in the Rust core. */

export function parseTagInput(raw: string): string[] {
  const out: string[] = [];
  for (const part of raw.split(/[,，;；\n\r]/)) {
    const tag = part.trim();
    if (!tag) continue;
    if (!out.some((old) => old.toLowerCase() === tag.toLowerCase())) out.push(tag);
  }
  return out;
}

export function toggleTag(tags: string[], tag: string): string[] {
  const clean = tag.trim();
  if (!clean) return tags;
  const exists = tags.some((old) => old.toLowerCase() === clean.toLowerCase());
  return exists
    ? tags.filter((old) => old.toLowerCase() !== clean.toLowerCase())
    : [...tags, clean];
}

/** 与旧版一致：优先本地记忆；查询到账号物品后再按标题精确匹配。 */
export function matchPublishedId(mod: LocalMod, items: ItemSummary[]): string | null {
  if (mod.publishedId && items.some((item) => item.id === mod.publishedId)) {
    return mod.publishedId;
  }
  const name = mod.name.trim().toLocaleLowerCase();
  return items.find((item) => item.title.trim().toLocaleLowerCase() === name)?.id ?? null;
}

export function emptyDraft(mod: LocalMod): Draft {
  const texts: Record<string, { key: string; title: string; description: string }> = {};
  for (const language of LANGUAGES) {
    texts[language.key] = {
      key: language.key,
      title: language.key === "english" ? mod.name : "",
      description: language.key === "english" ? mod.description : "",
    };
  }
  const existing = mod.publishedId;
  return {
    target: existing ? { kind: "existing", id: existing } : { kind: "new" },
    mode: "full",
    texts,
    tags: [...(mod.savedTags.length > 0 ? mod.savedTags : [])],
    keepTags: Boolean(existing),
    keepDescription: Boolean(existing),
    visibility: existing ? null : 2,
    changeNote: "",
    activeLanguage: "english",
  };
}

export function buildPlan(mod: LocalMod, draft: Draft): PublishPlan {
  const languages = Object.values(draft.texts)
    .filter((text) => text.title.trim().length > 0)
    .map((text) => ({
      key: text.key,
      title: text.title.trim(),
      description: text.description.trim(),
    }));
  return {
    modId: mod.id,
    target: draft.target,
    mode: draft.mode,
    languages,
    tags: draft.tags,
    keepTags: draft.keepTags,
    keepDescription: draft.keepDescription,
    visibility: draft.visibility,
    changeNote: draft.changeNote,
    contentFolder: draft.mode === "full" ? mod.packageRoot : "",
    previewPath: mod.savedPreview || mod.cover || "",
  };
}

export function targetLabel(plan: PublishPlan): string {
  return plan.target.kind === "new" ? "新建工坊物品" : `已有物品 · ${plan.target.id}`;
}

export function summarize(plan: PublishPlan): { label: string; value: string }[] {
  const visibility =
    plan.visibility === null
      ? "保留现有"
      : { 0: "公开", 1: "仅好友", 2: "隐藏", 3: "未列出" }[plan.visibility] ?? "未知";
  return [
    { label: "发布目标", value: targetLabel(plan) },
    {
      label: "模式",
      value: plan.mode === "full" ? "完整发布（含内容目录）" : "仅更新页面（不传内容）",
    },
    { label: "内容目录", value: plan.contentFolder || "不设置" },
    { label: "语言", value: plan.languages.map((l) => l.key).join(" → ") || "未填写" },
    {
      label: "TAG",
      value: plan.keepTags
        ? "保留现有"
        : plan.tags.length > 0
          ? plan.tags.join("、")
          : "清空现有",
    },
    { label: "描述", value: plan.keepDescription ? "保留现有" : "按本次文案覆盖" },
    { label: "可见性", value: visibility },
  ];
}
