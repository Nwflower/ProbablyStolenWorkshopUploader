import type {
  LocalMod,
  ModsView,
  PublishPlan,
  SteamEvent,
  SteamStatus,
} from "./types";

/**
 * Transport layer. Publish rules, manifest validation and Steam calls all live in
 * Rust; this module only forwards requests and listens for backend events.
 */

const isTauri = typeof window !== "undefined" && "__TAURI_INTERNALS__" in window;

async function call<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  const { invoke } = await import("@tauri-apps/api/core");
  return invoke<T>(command, args);
}

export const api = {
  isTauri,
  steamStatus: () => call<SteamStatus | null>("steam_status"),
  listMods: () => call<ModsView>("list_mods"),
  queryItems: (id: string) => call<void>("query_items", { id }),
  publish: (plan: PublishPlan) => call<void>("publish", { plan }),
  rememberPublished: (modId: string, id: string) =>
    call<boolean>("remember_published", { modId, id }),
  metaSummary: () =>
    call<{ path: string; modCount: number; communityNoticeSeen: boolean }>("meta_summary"),
  async listen(handler: (event: SteamEvent) => void): Promise<() => void> {
    if (!isTauri) return () => {};
    const { listen } = await import("@tauri-apps/api/event");
    const unlisten = await listen<SteamEvent>("steam-event", (payload) => handler(payload.payload));
    return unlisten;
  },
};

/** Browser-only layout shim. No validation, no Steam, no publish. */
export async function browserShim(): Promise<{ mods: ModsView; status: SteamStatus }> {
  const response = await fetch("/catalog.json");
  const catalog = (await response.json()) as {
    mods: Array<{
      id: string;
      name: string;
      subtitle: string;
      version: string;
      path: string;
      title: string;
      description: string;
      tags: string[];
      publishedId: string | null;
      valid: boolean;
    }>;
  };
  const mods: LocalMod[] = catalog.mods.map((mod) => ({
    id: mod.id,
    folder: mod.id,
    name: mod.name,
    author: "演示",
    version: mod.version,
    description: mod.description,
    path: mod.path,
    packageRoot: mod.path,
    assemblies: ["Demo.dll"],
    capabilities: [],
    errors: mod.valid ? 0 : 2,
    warnings: 0,
    issues: mod.valid
      ? []
      : [
          {
            level: "error",
            element: "<Assemblies>",
            message: "演示数据：缺少有效的程序集声明。",
            fix: "补上实际 DLL。",
            line: 0,
          },
        ],
    hasPreview: false,
    cover: null,
    publishedId: mod.publishedId,
    savedTags: mod.tags,
    savedPreview: "",
  }));
  return {
    mods: {
      game: {
        gameDir: "浏览器预览",
        modsDir: "浏览器预览 / Mods",
        source: "preview",
        exists: true,
      },
      mods,
      previews: {},
      error: "",
    },
    status: {
      connected: false,
      user: "",
      appId: 4348910,
      message: "浏览器预览不连接 Steam。",
      busy: false,
    },
  };
}
