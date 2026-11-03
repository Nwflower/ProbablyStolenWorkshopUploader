import { describe, expect, it } from "vitest";
import {
  buildPlan,
  emptyDraft,
  matchPublishedId,
  parseTagInput,
  summarize,
  targetLabel,
  toggleTag,
} from "./plan";
import type { LocalMod } from "./types";

const mod: LocalMod = {
  id: "demo",
  folder: "Demo",
  name: "演示",
  author: "作者",
  version: "1.0.0",
  description: "描述",
  path: "E:/Games/Mods/Demo",
  packageRoot: "E:/Games/Mods/Demo",
  assemblies: ["Demo.dll"],
  capabilities: [],
  errors: 0,
  warnings: 0,
  issues: [],
  hasPreview: false,
  cover: null,
  publishedId: "9007199254740993",
  savedTags: ["Mod", "UI"],
  savedPreview: "",
};

describe("tag helpers", () => {
  it("splits on mixed separators and dedupes case-insensitively", () => {
    expect(parseTagInput(" Mod, mod；UI ;  ")).toEqual(["Mod", "UI"]);
  });

  it("toggles a tag without duplicating case variants", () => {
    expect(toggleTag(["Mod"], "UI")).toEqual(["Mod", "UI"]);
    expect(toggleTag(["Mod", "UI"], "mod")).toEqual(["UI"]);
  });
});

describe("drafts", () => {
  it("starts an existing item with its published id and protected fields", () => {
    const draft = emptyDraft(mod);
    expect(draft.target).toEqual({ kind: "existing", id: "9007199254740993" });
    expect(draft.keepTags).toBe(true);
    expect(draft.keepDescription).toBe(true);
    expect(draft.visibility).toBeNull();
    expect(draft.texts.english.title).toBe("演示");
  });

  it("keeps large ids as strings end to end", () => {
    const plan = buildPlan(mod, emptyDraft(mod));
    expect(JSON.stringify(plan)).toContain('"9007199254740993"');
  });

  it("matches a remembered id before falling back to an exact title", () => {
    const items = [
      { id: "42", title: "演示", visibility: 0, tags: [] },
      { id: mod.publishedId!, title: "旧标题", visibility: 0, tags: [] },
    ];
    expect(matchPublishedId(mod, items)).toBe(mod.publishedId);
    expect(matchPublishedId({ ...mod, publishedId: null }, items)).toBe("42");
    expect(matchPublishedId({ ...mod, publishedId: null, name: "其他" }, items)).toBeNull();
  });
});

describe("plan building", () => {
  it("only submits languages with a title", () => {
    const draft = emptyDraft(mod);
    draft.texts.schinese.title = "   ";
    expect(buildPlan(mod, draft).languages.map((l) => l.key)).toEqual(["english"]);
    draft.texts.schinese.title = "名称";
    expect(buildPlan(mod, draft).languages.map((l) => l.key)).toEqual(["english", "schinese"]);
  });

  it("uses the package root for full uploads and nothing for page updates", () => {
    const draft = emptyDraft(mod);
    expect(buildPlan(mod, draft).contentFolder).toBe(mod.packageRoot);
    draft.mode = "page";
    expect(buildPlan(mod, draft).contentFolder).toBe("");
    expect(summarize(buildPlan(mod, draft)).find((r) => r.label === "模式")?.value).toContain(
      "不传内容",
    );
  });

  it("treats an explicit empty list as clearing tags", () => {
    const draft = emptyDraft(mod);
    draft.keepTags = false;
    draft.tags = [];
    const rows = summarize(buildPlan(mod, draft));
    expect(rows.find((r) => r.label === "TAG")?.value).toBe("清空现有");
    expect(targetLabel(buildPlan(mod, draft))).toContain("9007199254740993");
  });
});
