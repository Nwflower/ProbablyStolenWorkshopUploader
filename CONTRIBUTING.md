# 构建与贡献

本文命令均从独立仓库根目录执行。工作区内维护时使用相同的 `tools/WorkshopUploader/` 与 `tests/WorkshopUploaderTests/` 布局。

## 工程边界

上传器是独立的 WinUI 3 / C# 应用，不是游戏内 Mod。构建不依赖游戏安装、Python、`psmod.py` 或主工作区的配置。
`Program.cs` 是入口，`MainWindow` 负责 UI，`SteamUploader` 封装 Steam 调用，`WorkshopMeta` 保存本地设置，
`WorkshopVdf` 解析 VDF，`ManifestReader` 校验清单，`ModDiscovery` 扫描本地 Mod。
`packaging/Launcher.cs` 是程序分发包的外层启动器。

## 前置依赖

- Windows x64、PowerShell 7、.NET 8 SDK。
- Visual Studio 2022 或 Build Tools，安装 UWP/MSIX 构建组件和 Windows SDK。
- 按 `tools/WorkshopUploader/lib/README.md` 准备两个 Steam DLL。源码仓库和源码 ZIP 不包含它们。
- NuGet 会还原 csproj 固定版本的 Windows App SDK 和 Windows SDK BuildTools。

脚本通过 `vswhere` 查找 VS 2022 MSBuild，并查找标准安装位置中的 .NET 8 SDK；也可指定
`-DotnetRoot <SDK根目录> -MSBuildPath <MSBuild.exe路径>`。不要把本机路径写入源码。
WinUI 的 PRI/MSIX 构建任务需要 VS MSBuild，不能用纯 `dotnet build` 替代。

```powershell
pwsh tools/WorkshopUploader/build.ps1 -SelfContained
pwsh tools/WorkshopUploader/package.ps1
# 已有依赖缓存时可给上述命令追加：
# -RestoreSources "$env:USERPROFILE\.nuget\packages"
```

`build.ps1` 输出到工程内 `bin/`；`package.ps1` 在仓库 `scratch/` 下隔离构建，输出到 `dist/`。
分发 ZIP 包含 `app/`、启动器、使用说明、许可证和可独立使用的源码目录。
源码中的第三方依赖仍需按说明自行准备；运行包中的原生依赖按各自条款处理。

## 验证

```powershell
dotnet run --project tests/WorkshopUploaderTests/WorkshopUploaderTests.csproj --configuration Release
```

这些测试使用 `FakeSteam`，不连接 Steam、不创建或更新工坊物品；不需要第三方 DLL 或游戏。
CI 只运行这组测试，不把离线通过当成 UI 或真实 Steam 功能验收。
改动 UI 后需手动检查不同窗口宽度、缩放与主题。真实上传测试由维护者在 GUI 中自行进行。

## 源码导出

```powershell
pwsh tools/WorkshopUploader/export-source.ps1
```

脚本按白名单生成独立源码目录与 ZIP，拒绝覆盖已有目录。只复制上传器、离线测试、文档和 CI；
不复制 Git 历史、其他 Mod、DLL、bin/obj、日志或账号设置。新增必要文件时同步更新白名单。
不要将完整工作区推送为上传器仓库。版本由维护者决定，导出和打包都不会创建 Git 标签或自动提交。

## 提交约定

保持现有风格，说明具体问题、行为变化和验证范围。不要提交凭据、存档、个人路径或工坊账号数据。
原创贡献采用本项目的 `AGPL-3.0-only`；引入第三方代码或素材时记录来源并保留其许可。
