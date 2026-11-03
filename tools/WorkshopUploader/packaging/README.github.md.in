# Probably Stolen 创意工坊上传器

为《深空当铺 / Probably Stolen》制作的非官方 Windows 图形上传工具，作者：**千川白浪(Nwflower)**。

支持扫描本地 Mod、选择发布目标、导入 VDF、编辑工坊 TAG、多语言标题与描述、仅更新页面，以及 manifest 校验。
更新已有物品时默认保留现有描述、可见性和 TAG。

## 使用

完整解压程序分发包后，双击根目录的 `ProbablyStolenWorkshopUploader.exe`。
需要 Windows 10 1809+ / Windows 11 x64、.NET 8 桌面运行时，以及已登录且拥有游戏的 Steam 账号。
发布、更新与可见性修改由用户在 GUI 中手动执行。

[完整使用说明](tools/WorkshopUploader/README.md) · [构建与贡献指南](CONTRIBUTING.md)

## 工程范围

```text
tools/WorkshopUploader/       WinUI 3 源码、图标、构建与打包脚本
  lib/                       外部依赖准备说明；DLL 不随源码分发
  packaging/                 启动器、构建环境检测与导出模板
tests/WorkshopUploaderTests/  FakeSteam 离线回归测试
.github/workflows/            离线测试 CI，不发布工坊内容
LICENSE                      GNU AGPL v3 正文
THIRD-PARTY-NOTICES.md        第三方代码与素材权利说明
```

本仓库只包含上传器，不包含游戏程序集、Mod 工程、存档、账号设置、构建缓存或主工作区工具。
源码 ZIP 是开发材料；直接运行请使用程序分发包。

## 开发

从仓库根目录执行；完整构建的前置依赖见 [CONTRIBUTING.md](CONTRIBUTING.md)。

```powershell
# 不需要 Steam 或游戏的离线测试
dotnet run --project tests/WorkshopUploaderTests/WorkshopUploaderTests.csproj --configuration Release

# 先准备 lib 中的依赖，再构建和打包
pwsh tools/WorkshopUploader/build.ps1 -SelfContained
pwsh tools/WorkshopUploader/package.ps1
```

## 许可与联系

本项目原创代码使用 [GNU Affero General Public License v3.0 only](LICENSE)（`AGPL-3.0-only`）。
第三方依赖及游戏图标不纳入此授权，详见 [第三方声明](THIRD-PARTY-NOTICES.md)。
游戏图标的分发授权尚未确认；权利归游戏原权利人，注明归属不代表已获授权。

QQ群：1132567943。提交问题时请附复现步骤和脱敏日志，不要附 Steam 凭据、账号缓存或个人 Mod 文件。
