# Probably Stolen 创意工坊上传器

作者：**千川白浪(Nwflower)**。原创代码采用 [AGPL-3.0-only](LICENSE)，第三方依赖和游戏图标见 [权利声明](THIRD-PARTY-NOTICES.md)。

一个独立的图形界面工具，用来把《深空当铺 / Probably Stolen》的 mod 发布到 Steam 创意工坊（AppID 4348910）。

> 本目录是唯一保留上传操作说明的位置，仅供用户手动使用。Agent 不得据本文调用本程序或执行上传、更新和可见性修改。

界面是 **WinUI 3（Windows App SDK）**，用系统原生 Fluent 控件，不依赖游戏进程，也不受游戏内模组安全规则约束：
Rev6 起游戏内调用 `SteamUGC` 写入会被扫描器拒绝并由 tripwire 停用，所以发布只能在游戏外做。

## 功能

| 功能 | 说明 |
|---|---|
| 模组自动嗅探 | 定位 Steam 库 → **只扫描 `<游戏>/Mods/*`**（工坊订阅的副本不列出），列表带 2:1 缩略图与版本徽标 |
| 手动加入 | 「选择文件夹…」可临时加入任意含 `manifest.xml` 的目录 |
| 覆盖已有物品 | 分页查询本账号全部已发布物品，按本地记忆 → `ModId` 标签 → 标题匹配；也可输入物品 ID 或选「＋ 新建物品」 |
| 导入 VDF | 导入 `workshopitem` VDF 的 AppID、物品 ID、内容目录、预览图、可见性、标题、描述和更新说明；不支持的字段会提示 |
| 完整发布 | 上传内容、预览图和标题；已有物品默认保留原描述和可见性，可显式修改 |
| 更新说明 | 支持多行输入，换行会随提交传给 Steam |
| 仅更新页面 | 不调用 `SetItemContent`，不重传模组文件；可更新标题、预览图、描述或可见性 |
| 描述保护 | 已有物品默认勾选「保留工坊现有描述」；需要修改描述时取消勾选。新建或首次发布失败后的重试按输入文案发布 |
| 可见性 | 新建默认隐藏；更新默认不写可见性，也可显式选择公开 / 仅好友 / 未列出 / 隐藏 |
| 工坊 TAG 编辑 | 填写物品级分类 TAG（逗号分隔，附常用标签快捷按钮，上限 20 个）；已有物品默认「保留工坊现有 TAG」，取消勾选才按编辑内容整组覆盖，避免更新时误清空。TAG 只在第一轮提交时写入，与各语言标题/描述无关 |
| 分语言标题与描述 | 8 种语言下拉切换编辑；每语言一轮 `SetItemUpdateLanguage` + `SubmitItemUpdate`，English 作为默认语言最先提交 |
| 窗口布局 | 宽窗口并排、窄窗口上下布局；最小尺寸随显示缩放调整并跟随系统明暗主题 |
| 关于与社区 | 顶部「关于」显示作者 千川白浪(Nwflower) 和 QQ 群 1132567943；首次使用时展示一次群信息 |
| 日志 | 默认折叠，上传或出现错误时自动展开，也可随时手动展开和收起 |
| 本地记忆 | `%APPDATA%\ProbablyStolenWorkshopUploader\workshop-meta.json` 按 mod ID 记住物品 ID、预览图与各语言文案；新建取得 ID 后立即保存，即使后续上传失败也可重试 |
| 清单校验 | 按游戏加载器的判据检查 `manifest.xml`，一次列出全部问题并给出行号、期望值与改法；已选模组可点「查看清单问题」 |

## 运行要求

- Windows 10 1809+ / Windows 11，64 位
- **.NET 8 桌面运行时**
- **Windows App Runtime 1.7**（框架依赖版需要；自包含版不需要）
- **Steam 客户端在线且已登录**，账号拥有 Probably Stolen（AppID 4348910）

缺运行时时 Windows App SDK 的引导会弹窗并提供下载入口（跳到 Windows App SDK 下载页），不会静默崩溃。

> 已知坑：Windows App Runtime 若是由别的应用"顺带安装"的，可能只装了 Main/Singleton 而没有 DDLM，
> 未打包应用会以 `0x80670016` 失败。重装一次官方运行时即可补齐（1.1.3 起会装 DDLM）：
> [WindowsAppSDK#2595](https://github.com/microsoft/WindowsAppSDK/issues/2595)

## 使用

1. 启动 Steam，确认状态栏为**在线**
2. 双击 `ProbablyStolenWorkshopUploader.exe`
3. 左侧会自动列出本机 `Mods/` 里的模组；选中一个，或点「选择文件夹…」/「导入 VDF…」
4. 在「发布目标」选择已有物品或「＋ 新建物品」；也可点「输入物品 ID…」手动指定
5. 确认可见性、预览图、各语言标题与描述。悬停并点击预览图可更换图片；更新时默认保留工坊描述，想修改简介要取消勾选
6. 在「工坊 TAG」里填写或点常用标签补充分类；更新已有物品时默认保留现有 TAG，需要改动就取消「更新时保留工坊现有 TAG」再编辑
7. 「开始上传」= 完整发布；「仅更新页面」= 不重传模组文件。确认窗口会列出目标、可见性、描述和 TAG 处理方式
8. 完成后日志给出工坊页面链接；创建物品后会立即记住 ID。首次发布未完成时，重试将继续使用该 ID 并写入所填描述

自动扫描的是游戏目录里的 `Mods/`；修改或构建工作区源码不会自动更新这个目录。上传工作区产物时请用「选择文件夹…」选中实际发布包，并在确认窗口核对文件夹路径和版本。

VDF 只支持 Probably Stolen 的 AppID `4348910`，必须包含 `publishedfileid`（新建填 `0`）和有效的 `contentfolder`；指定预览图时文件也必须存在。`tags` 支持嵌套写法（`"tags" { "0" "Mod" "1" "Gameplay" }`）或单行逗号分隔（`"tags" "Mod,Gameplay"`），导入后会取消「保留现有 TAG」。导入的标题和描述进入英文栏。已有物品默认仍保留工坊描述，需要更新导入的描述时请取消勾选。

> 首次上传前需要先在网页上接受一次创意工坊法律协议；上传到**运行者自己登录的 Steam 账号**名下，只能更新自己发布的物品。

## 清单校验

选文件夹、导入 VDF、点「开始上传」时都会校验 `manifest.xml`。判据直接对齐游戏加载器（`Assembly-CSharp` 里的
`ModLoader.ParseManifestCore` 与 `ModLoader.IsWellFormed`），所以“这里报错”等于“游戏里加载不了”。报错会一次列出**全部**问题，
每条带行号/列号、期望值、实际值和改法，可「复制到日志」。

| 判据（与游戏一致） | 不满足时 |
| --- | --- |
| `manifest.xml` 存在，且是合法 XML | 错误，指出报错行号与未闭合/对不上的标签名 |
| `<ID>` 必填，只允许字母、数字、`_`、`-`、`.`，不以 `.` 开头，≤64 字符 | 错误，指出第几个字符不合法并给出建议 ID |
| `<Assemblies>` 至少一个 `<Assembly>`，路径为包内相对 `.dll`，不含 `..`、不是绝对路径 | 错误 |
| 列出的 DLL 存在，且至少一个含 `IMod` 实现 | 错误 |
| `<Capabilities>` 必须存在（可为空），能力名只能是 `files`／`network`／`process`／`dynamic-code`／`native`／`reflection-external`／`steam` | 错误 |
| 包里没有未列入 `<Assemblies>` 的 DLL（游戏只扫包根与 `Assemblies/`） | 错误：游戏会注册但拒绝启用 |

游戏**不**校验根节点名、`Name`／`Author`／`ModVersion`／`Description`，也不按 `GameVersions` 拦截，所以这些只出警告；
带 XML 命名空间的根节点会让游戏把字段全当成缺失，按错误处理。

> `psmod.py validate --route official` 服务的是工作区规范（要求小写 ID、要求全部元数据元素），比游戏严格；
> 上传器只保证“游戏加载器能加载并启用”。两者不一致时以本工具为准判断能否发布。

## 自检与排查

```powershell
# 仅验证 Steam 初始化与账号（不联网上传）
.\ProbablyStolenWorkshopUploader.exe --selftest

# 只读：查询本账号在该 AppID 下已发布的物品（不创建、不修改）
.\ProbablyStolenWorkshopUploader.exe --querytest
```

结果分别写入 exe 同目录的 `selftest.log` / `querytest.log`。

如果提交超过两分钟没有新进度且 Steam 尚未返回最终结果，界面会显示「提交结果未确认」。这不是确认失败：Steam 仍可能完成提交，请先检查物品页面和更新记录，再决定是否重试。

首次欢迎提示的已查看状态保存在本地记忆文件中，与模组发布信息一起保留。

### 分语言的代价

每种语言都是一轮完整的 `SubmitItemUpdate`。只改文案的轮次不会重传内容，但**每次提交都会给订阅者推一次更新**，并且 Steam 有节流，所以工具在轮次之间留了 1.2 秒间隔。实际使用时建议只填确实需要的语言（英文 + 简中通常已覆盖大多数玩家）。

## 结构

| 文件 | 职责 |
|---|---|
| `Program.cs` | 入口（自定义 Main，禁用 XAML 生成入口）；`--selftest` / `--querytest` 自检模式 |
| `App.xaml` / `App.xaml.cs` | Application 与全局资源（卡片样式、列表模板） |
| `build.ps1` / `package.ps1` / `export-source.ps1` | 构建、程序打包与独立源码导出 |
| `MainWindow.xaml` / `.xaml.cs` | 界面、模组列表、多语言编辑器、发布流程编排 |
| `ModDiscovery.cs` | 注册表 → `libraryfolders.vdf` 定位 Steam 库，扫描本地与工坊模组 |
| `SteamUploader.cs` | SteamUGC 调用封装（CreateItem / StartItemUpdate / SetItemUpdateLanguage / SubmitItemUpdate / 查询） |
| `WorkshopMeta.cs` | 按 mod ID 的本地记忆（DataContractJsonSerializer） |
| `WorkshopVdf.cs` | `workshopitem` VDF 的解析和输入校验 |
| `ManifestReader.cs` | 按游戏加载器判据校验 `manifest.xml`，一次列出全部问题（行号／期望／实际／改法） |
| `lib/` | `Steamworks.NET.dll` 20.2.0 + `steam_api64.dll` |
| `steam_appid.txt` | 内容为 `4348910`，必须与 exe 同级 |

## 开发与源码

构建依赖、工程边界、离线测试和贡献约定见 [CONTRIBUTING.md](CONTRIBUTING.md)。
独立仓库保留 `tools/WorkshopUploader/` 与 `tests/WorkshopUploaderTests/` 的布局；
无需整个 Mod 工作区、游戏程序集或 Python 工具。

从仓库根目录执行：

```powershell
# 离线测试，不连接 Steam
dotnet run --project tests/WorkshopUploaderTests/WorkshopUploaderTests.csproj --configuration Release

# 按 lib/README.md 准备依赖后打包程序
pwsh tools/WorkshopUploader/package.ps1

# 导出供 GitHub 使用的源码目录与 ZIP
pwsh tools/WorkshopUploader/export-source.ps1
```

程序包和源码包输出到 `dist/`。程序包根目录包含启动器、`app/`、`docs/` 和使用说明；
`app/` 保留简体和繁体中文独立语言资源。解压全部文件后双击根目录 EXE。
`docs/Source/` 包含对应源码、离线测试和构建说明；第三方 DLL 不进入源码包。

## 许可

原创代码 Copyright (c) 2026 千川白浪(Nwflower)，使用 [GNU AGPL v3](LICENSE)（`AGPL-3.0-only`）。
Steamworks.NET、Steam 原生接口、Microsoft 运行时及游戏图标分别遵循各自许可，详见
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。游戏图标分发授权尚未确认，注明权利归属不代表已获授权。
