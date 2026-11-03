# 第三方声明

本项目原创代码使用 AGPL-3.0-only（见 [LICENSE](LICENSE)）。下列组件各自遵循其许可，
本项目不改变、也不额外限制这些许可。

## 运行时与构建依赖

| 组件 | 许可 | 用途 |
| --- | --- | --- |
| [Tauri](https://tauri.app/)（`tauri`, `tauri-build`） | MIT / Apache-2.0 | 桌面窗口与 IPC |
| [React](https://react.dev/) | MIT | 界面 |
| [Vite](https://vite.dev/) | MIT | 前端构建 |
| [lucide](https://lucide.dev/) | ISC | 图标 |
| [serde](https://serde.rs/)、`serde_json` | MIT / Apache-2.0 | 序列化 |
| Windows WebView2 运行时 | Microsoft 软件许可 | 由系统提供，不随本项目分发 |
| `steam_api64.dll` | Valve Steamworks SDK 可再分发组件 | 与目标游戏一起分发，仅用于同一 AppID 的 Steam 接口 |

Rust 与 npm 依赖的完整版本见 `Cargo.lock` 与 `package-lock.json`。
发布二进制时须一并提供本文件与 AGPL 正文。

## 游戏相关内容

- 《深空当铺 / Probably Stolen》的名称、图标与游戏素材归其权利人所有，不在本项目授权范围内。
- 当前仓库使用 Tauri 模板生成的占位图标，尚未引入游戏素材。
- 若后续加入游戏图标或素材，需单独确认分发授权，并在本文件注明归属。
- 与 Steam 交互时须遵守 Steam 创意工坊条款；上传目标只能是运行者自己登录并拥有的账号。

## 数据

`tests/fixtures/` 与 `public/catalog.json` 是人工编写的演示数据，不包含真实玩家、
账号、存档或工坊物品信息。
