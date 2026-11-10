# 依赖与参考核查 · 2026-11-10

| 依赖 | 固定版本 | License | 实际用途 |
|---|---|---|---|
| .NET / WPF | net10.0-windows；本机 SDK 10.0.403 | MIT 与发行物 Notices | LTS、自包含 win-x64 |
| CmlLib.Core | 4.1.0 | MIT | 下载、校验、Java、启动参数与真实进程；内置 FabricInstaller |
| CmlLib.Core.Auth.Microsoft | 3.4.0 | MIT | Xbox / XSTS / Minecraft Java 认证与游戏账号缓存 |
| CmlLib.Core.Installer.Forge | 2.0.0 | MIT | Forge 与 NeoForge 官方安装机制；使用公开 API |
| XboxAuthNet.Game.Msal | 0.2.0 | MIT | MSAL 与 CmlLib 认证链桥接 |
| Microsoft.Identity.Client | 4.92.0 | MIT | 官方 MSAL、系统浏览器、Token Refresh；自建 DPAPI cache callbacks |
| ProtectedData | 10.0.0 | MIT | Windows DPAPI CurrentUser |
| Microsoft.Extensions.DependencyInjection | 10.0.14 | MIT | UI 组合根 |
| Serilog / Sinks.File | 4.4.0 / 7.0.0 | Apache-2.0 | 滚动日志；不记录原始认证异常和命令行 |
| xUnit / VS Runner / Test SDK | 2.9.3 / 4.0.1 / 18.11.0 | Apache-2.0 / Apache-2.0 / MIT | 仅测试 |

API 在编码前通过官方文档、NuGet 稳定索引与库源码核实；实际还原、编译和联网安装进一步验证。NeoForge 使用该包公开的 NeoForgeInstaller，已验证 1.21.1。没有独立 CmlLib Fabric NuGet 依赖，使用 Core 内置支持。Forge 高层 Install 会打开广告 URL，本项目使用公开版本映射器和 IForgeInstaller，不触发广告包装逻辑。

参考成熟启动器的产品流程，但未复制其 GPL 实现代码：Prism 的实例隔离、目标实例明确化、按 Minecraft/Loader 筛选 Mod、启停与备份思路。实现使用自身 C# 模块和 MIT 依赖。

- https://github.com/CmlLib/CmlLib.Core
- https://github.com/CmlLib/CmlLib.Core.Auth.Microsoft
- https://github.com/CmlLib/CmlLib.Core.Installer.Forge
- https://prismlauncher.org/wiki/getting-started/download-mods/
- https://prismlauncher.org/wiki/help-pages/loader-mods/
- https://docs.modrinth.com/api/operations/searchprojects/
- https://docs.modrinth.com/api/operations/getprojectversions/
- https://support.modrinth.com/en/articles/8802351-modrinth-modpack-format-mrpack
- https://docs.curseforge.com/rest-api/
- https://learn.microsoft.com/entra/msal/dotnet/acquiring-tokens/using-web-browsers
- https://dotnet.microsoft.com/platform/support/policy/dotnet-core

packages.lock.json 固定完整解析结果；dependency-manifest.json 记录发布依赖，licenses/ 包含包元数据与许可文本。上游发布可用不等于所有 Minecraft / Mod 组合已验收，实际边界见 TEST-REPORT.md。
