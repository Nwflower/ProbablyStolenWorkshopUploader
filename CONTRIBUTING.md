# 参与开发

## 环境

运行 `pwsh scripts/env.ps1` 会把工程内的 Rust 工具链加入当前会话的 PATH。
需要 .NET 之外的依赖时按 [README](README.md) 的表格准备。

## 提交前检查

```powershell
pwsh scripts/check.ps1
```

它会依次执行：

1. `cargo fmt --all -- --check`
2. `cargo test -p uploader-core --locked`
3. `npm run build`（含 TypeScript 类型检查）
4. `npm test`（Vitest）

桌面外壳单独编译（必须用 Tauri CLI，直接用 cargo build 会嵌成开发服务器地址）：

```powershell
pwsh scripts/desktop.ps1 build --no-bundle
```

便携包输出到 `release/portable/`：

```powershell
pwsh scripts/package.ps1
```

`steam/steam_api64.dll` 需要与目标游戏版本匹配；过旧的 DLL 会因为缺少入口点导致启动失败。

## 代码边界

- 发布规则放在 `crates/uploader-core`，用单元测试覆盖；不要在 React 里复制一份。
- 跨 IPC 的 64 位工坊 ID 一律用十进制字符串，Rust 内部用 `u64`。
- 新增命令时同时考虑：参数校验、任务状态、迟到回调、错误可读性。
- 不要提交 Steam 原生库、账号数据、游戏程序集、存档或本机路径。
- 与 Steam 相关的真实写入只能在用户手动操作的桌面窗口里发生。

## 界面

- 主题跟随系统，保留 Windows 标准窗口操作。
- 状态同时用文字和图标表达，不只靠颜色。
- 新增控件检查 100%/150%/200% 缩放、键盘 Tab 顺序、中文输入法。

## 测试

Rust 测试使用合成数据；前端测试只覆盖展示层逻辑。演示数据放在 `tests/fixtures/`，
不要放入真实玩家目录或账号信息。
