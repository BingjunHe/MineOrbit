# MineOrbit 0.5.0

MineOrbit is a personal Windows desktop launcher for Minecraft: Java Edition, built with C# / .NET 10 and WPF. It supports isolated game versions, mod management, and modpack import. Gameplay requires Microsoft authentication and verification of Java Edition ownership; offline authentication and license bypasses are not provided. Tokens are stored locally with Windows DPAPI encryption.

**Authentication status:** The last MineOrbit authentication check on 2026-11-10 reached Minecraft Services and returned HTTP 403. Approval status has not been rechecked in this export-focused update. Version 0.5.0 prepares independent game folders and registers them with the official launcher, which performs its own normal account authentication. See [login setup](docs/MICROSOFT-LOGIN.md) and [validation report](docs/TEST-REPORT.md).

个人使用的 Minecraft Java Edition Windows 10/11 x64 启动器。C# / .NET 10 LTS / WPF / MVVM。提供可编译源码，可构建为自包含 Windows 发布包。

GitHub 源码仓库不包含游戏数据、账号缓存、下载依赖或发布压缩包。Windows 上可运行 `./scripts/build.ps1 -Publish` 构建并测试；仓库的 Windows build 工作流也会生成可下载的构建产物。

## 运行

解压 `artifacts/MineOrbit-0.5.0-release.zip`，运行其中的 `MineOrbit.exe`；保留全部文件，不要只复制 EXE。已包含 .NET 桌面运行时。旧版 MineDeck 的设置在新配置不存在时可以读取，旧账号缓存不会自动迁移，需要重新登录。新配置位于 `%LOCALAPPDATA%\MineOrbit`。

## 正版登录与启动

1. 此版本已接入 MineOrbit 的应用 Client ID，可直接点击“登录 Microsoft”；设置仍允许替换为自建应用。旧配置为空时自动使用内置应用 ID。登录和启动会先保存并应用当前设置。
2. 应用应是支持个人 Microsoft 账号的 public/native 桌面应用，配置回调 **http://localhost**。不需要、也不应配置 Client Secret。Minecraft Services 另有应用准入要求；仅有 Entra 应用注册并不保证获准访问 Minecraft API。
3. 点击“登录 Microsoft”，按设置中的“浏览器登录”或“设备码登录”完成官方授权。点击启动时会先刷新会话，需要时自动引导重新登录；缺少应用配置时会打开注册帮助。MineOrbit 不提供密码输入表单。
4. 到“下载 Minecraft”，获取版本列表，选择 Vanilla / Fabric / Forge / NeoForge，获取兼容 Loader 版本并安装。
5. 主页选择游戏版本，点击“启动游戏”。程序刷新 MSAL 会话、完成 Xbox / XSTS / Minecraft Java 认证、补齐依赖、检查 x64 Java 版本、构建参数并启动真实进程。

账号通过 Minecraft Java 认证后才标为“已认证”。没有正版权限、有效 Profile、API 授权或 Client ID 时会明确失败，不会切换为离线/伪造会话。

**2026-11-10 的 MineOrbit 认证验证在 Microsoft 授权后收到 Minecraft HTTP 403，尚未完成 MineOrbit 游戏主菜单验收；0.5.0 未重新检查审批状态。** 应先确认应用的 Minecraft API 准入和账号限制；不能把微软网页登录成功当作 Minecraft 登录成功。用户需要在官方页面本人授权，不要在聊天中发送密码、Token 或 Client Secret。

参考：[CmlLib MSAL 登录文档](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game.msal/oauth/)、[应用 ID 与 Minecraft API 准入](https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game.msal/clientid/)、[Microsoft 桌面应用注册](https://learn.microsoft.com/entra/identity-platform/how-to-add-redirect-uri)。

## 版本隔离

安装时可选择隔离方式；现有游戏版本在“游戏版本 → 版本隔离”中调整。

| 方式 | Mods / Config / Saves 等游戏数据 |
|---|---|
| 独立游戏版本隔离（默认推荐） | 每个游戏版本使用自己的 GUID 目录，即便 Minecraft / Loader 相同也互不影响 |
| 按 Minecraft + Loader 版本隔离 | 相同 Minecraft、Loader 类型与 Loader 版本共享一组游戏内容；不同组合分开 |
| 共享游戏目录（不隔离） | 所有选择此项的游戏版本共用 `game-data/shared`，并明确限制不兼容 Mods 混装 |

目录选择真实作用于游戏启动的 `game_directory`、工作目录、Mods 安装、备份和“打开文件夹”，不是仅改变界面文字。游戏运行时持有目录锁，防止其他 MineOrbit 进程修改同一目录中的 Mods。

切换隔离方式**不会搬迁或删除原目录文件**；切换后可能看到新的空存档目录，可随时切回。操作前建议备份。整合包导入一律独立隔离。

## Mods

1. 在 Mods 页明确选择目标游戏版本。Vanilla 不支持 Loader Mods，需使用 Fabric / Forge / NeoForge。
2. 默认“自动选择可用渠道”，并行查询 Modrinth 和已配置 Key 的 CurseForge；也可以指定单一渠道。每项显示来源，不按同名强行合并项目。搜索结果按 Minecraft 与 Loader 筛选。
3. 获取兼容版本，选择后安装。确认窗口展示目标目录及将安装的 Mod / 必要依赖。
4. 下载使用 Hash 校验；依赖处理包括固定版本、必要依赖、循环与冲突检查。版本或 Loader 不兼容、Modrinth 声明仅服务端、无法确定主文件时拒绝静默安装。
5. 刷新已安装列表后，可更新、禁用/启用或删除。未知来源的本地 JAR 可管理启用状态和删除，但不会猜测其 Modrinth 项目来自动更新。

自动模式下载失败时，会在另一官方渠道寻找大小和校验值一致的文件，再校验下载内容；找不到相同文件会失败，不按文件名替换版本。CurseForge 搜索和 CDN 下载都需要有效授权 Key，未配置时自动使用 Modrinth。

文件变更采用可回滚事务，遇到中断会在下次操作/启动前恢复。更新前若已管理文件被外部修改而 Hash 不一致，会停止覆盖。禁用或删除依赖仍可能影响其他 Mods；界面会提醒，建议先备份。

## ZIP / MRPACK 整合包

拖入文件或选择压缩包 → 查看识别结果 → 填写新游戏版本名 → 开始安装。

- **MRPACK**：从清单读取 Minecraft、Fabric/Forge/NeoForge，下载声明文件、校验 SHA512、处理客户端 required/optional/unsupported、覆盖 `overrides` 后应用 `client-overrides`。客户端可选文件由复选框控制。未知 Loader（例如 Quilt）明确拒绝。
- **完整文件 ZIP**：支持外层文件夹、.minecraft / minecraft 目录和单个按版本隔离的游戏目录。从唯一的版本配置识别 Minecraft / Loader，无法唯一识别时要求手动填写；包含 Mods 时拒绝按原版误导入。只复制游戏内容，旧运行库、版本配置和账号文件不导入。
- **MultiMC / Prism ZIP**：读取 mmc-pack.json 的官方 Minecraft、Fabric / Forge / NeoForge 组件，导入 .minecraft 游戏内容；未知组件和自定义版本补丁明确拒绝。`instance.cfg` 启动命令不会执行。
- **CurseForge Export**：通过授权 API Key 获取文件；Key 在设置中输入后以 DPAPI 加密保存。API 不提供下载 URL、作者限制第三方下载或缺少授权时，列明项目/文件 ID 并终止，不绕过限制，不把缺文件游戏版本标为完成。

导入检查路径穿越、绝对路径、Windows 设备名、重复路径、符号链接、文件数、压缩比、总大小和单文件大小。下载清单不得覆盖启动器元数据或运行库；解压过程再次限制实际字节数。安装在全新 GUID 目录中进行，完整成功之后才发布游戏版本配置；失败清理本次目录，不修改已有游戏版本。

## 导出为其他启动器可用的文件夹

在“整合包”页选择 ZIP / MRPACK、检查版本信息，选择新的空文件夹，点击“将整合包导出到文件夹”。会安装并复制本体、Loader、所需 Libraries / Assets、Mods、配置及可用的 bundled Java。导出不需要 MineOrbit 登录。

使用官方启动器时，先关闭官方启动器，在此页面点击“添加到官方启动器”。程序会备份原配置并生成独立版本和启动项，游戏运行目录指向导出文件夹。重新打开官方启动器，在 Java 版安装配置中选择整合包并使用正版账号启动。

PCL / HMCL 需添加该 Minecraft 根目录，并将实际游戏运行目录设为此文件夹。详细步骤和验证边界见 [文件夹导出说明](docs/PACK-FOLDER-EXPORT.md)。

## 其他完善

- 游戏版本重命名、复制为独立游戏版本、打开真实游戏目录 / Mods / 日志。
- 备份存档、Mods、Config、资源包、截图和 options；备份 ZIP 保存在 `DataRoot/backups`。
- 移除游戏版本需二次确认，仅移除配置，保留所有存档与文件。
- 下载真实文件任务进度、取消、有限自动重试、Hash 缓存复用。
- 普通 Minecraft 安装保留 `.pending.json`，可在重启后到“下载任务”恢复；整合包导入尚不支持进程被强制终止后的续传。
- Java 自动 Runtime 下载、本机 Java 检测，以及启动前 x64 / Java 主版本检查。
- 内存、窗口分辨率、下载并发、数据路径与缓存路径配置。

## 数据与缓存

默认游戏数据根目录：`D:\Minecraft\MineOrbit`。实际数据路径可在设置修改，保存后生效，不自动搬迁原文件。

- `instances/<GUID>`：游戏版本元数据、版本配置与独立游戏数据。
- `game-data/<分组>`：按版本隔离/共享模式的游戏数据。
- `libraries`、`assets`、`runtime`：可共享的只读运行依赖。与游戏存档隔离是两个不同设置；可关闭共享或分别自定义路径。
- `cache/<算法>/<Hash>`：经过校验的 Mod / 整合包文件缓存，可自定义。
- `backups`：用户主动创建的备份。
- `%LOCALAPPDATA%\MineOrbit\settings.json`：设置引导文件。
- `%LOCALAPPDATA%\MineOrbit\settings.json.secrets`：DPAPI 加密的 CurseForge Key。
- `%LOCALAPPDATA%\MineOrbit\accounts\<Client ID>\accounts.dpapi`、`msal.dpapi`：DPAPI CurrentUser 加密的账号与 MSAL 缓存。
- `%LOCALAPPDATA%\MineOrbit\logs`：滚动启动器日志。
- 实际游戏目录的 `logs/latest.log` 与 `.mineorbit/last-output.log`：游戏日志与最近进程输出。应用不记录登录 Token、完整命令行或原始认证响应。

退出账号清除本应用账号缓存，不删除浏览器中的 Microsoft Cookie。不读取官方启动器或 PCL2 的 Token / 游戏文件。集成测试仅操作项目内 `artifacts/smoke-data`。

## 构建与测试

需要 .NET 10 SDK，Visual Studio 2026 或兼容版本；打开 `MineOrbit.sln`。

```powershell
./scripts/build.ps1
./scripts/build.ps1 -Publish
./scripts/package-source.ps1
```

脚本优先使用项目内 `.tools/dotnet/dotnet.exe`，否则使用系统 dotnet。可手动执行：

```powershell
dotnet restore MineOrbit.sln
dotnet build MineOrbit.sln -c Release -m:1
dotnet test tests/MineOrbit.Tests -c Release
dotnet publish src/MineOrbit.UI -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o artifacts/MineOrbit-0.5.0-release
```

联网验证工具（会真实下载文件，不登录账号、不启动游戏）：

```powershell
dotnet run --project tools/MineOrbit.Smoke -c Release
dotnet run --project tools/MineOrbit.Smoke -c Release -- --install
dotnet run --project tools/MineOrbit.Smoke -c Release -- --forge
dotnet run --project tools/MineOrbit.Smoke -c Release -- --mods
dotnet run --project tools/MineOrbit.Smoke -c Release -- --pack
dotnet run --project tools/MineOrbit.Smoke -c Release -- --neo
```

`--forge` / `--mods` 使用 `--install` 创建的隔离测试游戏版本；`--pack` / `--neo` 使用自己的子目录。切勿把真实游戏目录作为测试工具第二参数。首次联网测试可能下载数 GB。

## 验证边界与剩余事项

详见 [测试报告](docs/TEST-REPORT.md)。主要代码已连接真实服务，不代表所有 Minecraft / Mod 组合已验证。尚需真人授权后的端到端游戏启动验证、带授权 Key 的 CurseForge 实测、更多 Windows 10/11 / DPI 和历史版本兼容验证。界面目前只有深色主题，未实现 Light Theme；未提供整合包自动升级、任务队列调度、字节级断点续传、文件物理删除 UI 或自动清理所有历史缓存。

架构：Core 模型与规则；Infrastructure 按认证、Minecraft、Mods、依赖、缓存、文件事务、整合包、备份和存储拆分服务；UI 为 WPF / MVVM / DI；Tests 为隔离单元测试；Smoke 为显式联网验证。Minecraft 启动协议由 CmlLib 提供。依赖版本见 [依赖说明](docs/DEPENDENCIES.md)，许可见 [第三方说明](THIRD-PARTY-NOTICES.md) 与 licenses。

0.3 更新：原创方块轨道图标、EXE 与窗口图标；界面统一称“游戏版本”；隐藏上下滚动条并保留滚轮滚动；首次启动授权恢复、设备码登录和内置 Client ID 帮助。注册步骤见 [MICROSOFT-LOGIN.md](docs/MICROSOFT-LOGIN.md)。图标源文件位于 assets 与 src/MineOrbit.UI/Assets，运行 tools/MineOrbit.Branding 可重新生成 ICO / PNG。

0.3.1 修复：小窗口侧栏紧凑布局、内容滚动与列表边缘滚轮传递；底部固定保存设置与翻页按钮。设置输入即时提交，正常关闭窗口自动保存；无效输入或写入失败会明确提示并保留窗口。记住上次页面、所选游戏版本与窗口大小。CurseForge Key 仍加密保存且不回显。

0.4.0 更新：接入 MineOrbit 应用 ID、自动应用设置、账号按应用分开加密缓存、登录超时与阶段诊断、缺失启动配置修复、游戏进程状态与退出诊断、固定主页启动按钮；自动 Modrinth / CurseForge 渠道及同 Hash 文件回退；完善包装目录 / .minecraft / 隔离目录 / MultiMC ZIP 识别导入；更新蓝绿卡片界面。

0.5.0 更新：整合包导出为完整标准目录、复制所需资源和运行库、官方启动器独立启动项、原配置备份、重复注册及路径迁移。101 项测试通过，并完成真实 Fabric 整合包的无网络文件完整性验证。
