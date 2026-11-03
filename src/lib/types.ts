export type Issue = {
  level: "error" | "warning";
  element: string;
  message: string;
  fix: string;
  line: number;
};

export type LocalMod = {
  id: string;
  folder: string;
  name: string;
  author: string;
  version: string;
  description: string;
  path: string;
  packageRoot: string;
  assemblies: string[];
  capabilities: string[];
  errors: number;
  warnings: number;
  issues: Issue[];
  hasPreview: boolean;
  cover: string | null;
  publishedId: string | null;
  savedTags: string[];
  savedPreview: string;
};

export type GameLocation = {
  gameDir: string;
  modsDir: string;
  source: string;
  exists: boolean;
};

export type ModsView = {
  game: GameLocation | null;
  mods: LocalMod[];
  previews: Record<string, string>;
  error: string;
};

export type SteamStatus = {
  connected: boolean;
  user: string;
  appId: number;
  message: string;
  busy: boolean;
};

export type ItemSummary = {
  id: string;
  title: string;
  visibility: number;
  tags: string[];
};

export type Target = { kind: "new" } | { kind: "existing"; id: string };
export type PublishMode = "full" | "page";
export type LanguageText = { key: string; title: string; description: string };

export type PublishPlan = {
  modId: string;
  target: Target;
  mode: PublishMode;
  languages: LanguageText[];
  tags: string[];
  keepTags: boolean;
  keepDescription: boolean;
  visibility: number | null;
  changeNote: string;
  contentFolder: string;
  previewPath: string;
};

export type TaskStatus = "running" | "succeeded" | "failed" | "unconfirmed";

export type TaskSnapshot = {
  id: string;
  status: TaskStatus;
  progress: number;
  round: number;
  totalRounds: number;
  language: string;
  message: string;
  confirmedLanguages: string[];
  logs: string[];
};

export type SteamEvent =
  | { kind: "status"; status: SteamStatus }
  | { kind: "log"; line: string }
  | { kind: "items"; items: ItemSummary[]; truncated: boolean }
  | { kind: "task"; task: TaskSnapshot }
  | { kind: "published"; modId: string; id: string };

export type Language = { key: string; label: string };

export type Draft = {
  target: Target;
  mode: PublishMode;
  texts: Record<string, LanguageText>;
  tags: string[];
  keepTags: boolean;
  keepDescription: boolean;
  visibility: number | null;
  changeNote: string;
  activeLanguage: string;
};

export const LANGUAGES: Language[] = [
  { key: "english", label: "English" },
  { key: "schinese", label: "简体中文" },
  { key: "russian", label: "Русский" },
  { key: "spanish", label: "Español" },
  { key: "french", label: "Français" },
  { key: "german", label: "Deutsch" },
  { key: "polish", label: "Polski" },
  { key: "japanese", label: "日本語" },
];

export const SUGGESTED_TAGS = [
  "Mod",
  "Gameplay",
  "UI",
  "Quality of Life",
  "Translation",
  "Tools",
  "Utilities",
  "Chinese",
  "Music",
  "Mono",
];

export const VISIBILITY = [
  { value: 0, label: "公开" },
  { value: 1, label: "仅好友" },
  { value: 2, label: "隐藏" },
  { value: 3, label: "未列出" },
] as const;
