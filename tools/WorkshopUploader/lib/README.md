# 本地 Steam 依赖

完整 Windows 构建需要在本目录准备：

| 文件 | 要求与来源 |
| --- | --- |
| `Steamworks.NET.dll` | Steamworks.NET 20.2.0 独立 .NET 版本，来自[上游发行版](https://github.com/rlabrecque/Steamworks.NET/releases/tag/20.2.0)或对应源码构建；不要使用 Unity 专用程序集 |
| `steam_api64.dll` | 与绑定匹配的 Windows x64 Steam 原生接口，来自有权使用的 Steamworks SDK / 对应依赖分发；不要用 32 位 `steam_api.dll` |

不要提交这两个 DLL。源码导出会排除它们，离线测试使用 FakeSteam，不需要它们。
构建会把 DLL 复制到应用输出目录，运行需要 Steam 客户端；不会修改游戏目录。
依赖的权利与许可见 [第三方说明](../THIRD-PARTY-NOTICES.md)。
