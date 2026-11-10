# MineOrbit 0.2 验证报告 · 2026-11-10

环境：本机真实 Windows x64；.NET SDK 10.0.403 / .NET 10 LTS；WPF；自包含 win-x64 发布。游戏联网测试仅使用本项目 artifacts/smoke-data，未读取或修改官方启动器 / PCL2 资源。界面检查仅对现有实例进行只读搜索，没有向用户现有实例安装测试 Mods。

| 检查 | 结果 | 具体边界 |
|---|---|---|
| MineOrbit 解决方案 Release 构建 | 通过，0 警告 / 0 错误 | 已核对实际 NuGet API |
| xUnit | 47 / 47 通过 | 隔离目录、真实 Windows DPAPI、依赖与事务规则；网络失败/取消使用测试处理器，仅证明错误处理 |
| Mojang / Fabric / Forge / NeoForge 列表 | 通过 | 861 个 Minecraft 版本；1.20.1 Fabric 255 项、Forge 138 项；1.21.1 NeoForge 260 项（测试时快照） |
| Vanilla + 官方 Java | 实际下载成功 | 游戏资源 / Libraries / Runtime；不代表游戏已运行 |
| Fabric | 实际安装成功 | 1.20.1 + Fabric 0.20.1 |
| Forge | 实际安装成功 | 1.20.1 + Forge 47.4.29（前阶段完整安装复用） |
| NeoForge | 实际安装成功 | 1.21.1 + NeoForge 21.1.265，完整安装处理器执行 |
| Modrinth 在线搜索 / 版本 / 必要依赖 | 通过 | Iris 1.7.6 for 1.20.1 → Sodium 0.5.12 Beta 2 |
| Mod 文件 Hash / 启用禁用 / 重装 / 删除 | 通过真实下载验证 | 安装 2 个真实 Mod，禁用再启用 Iris，重复安装校验后移除 Iris |
| MRPACK 完整导入 | 通过真实联网验证 | Minecraft 1.20.1 + Fabric + Fabric API，SHA512 校验、实例发布、client-overrides 优先级 |
| 普通 ZIP 完整导入 | 通过实际验证 | 手动声明 1.20.1 / Fabric 0.20.1，安装并验证 config 内容 |
| 共享缓存与 Java 检查 | 通过 | Libraries/Assets/Runtime 跨实例共用，Mod Hash 缓存复用；再次运行 MRPACK 与普通 ZIP 验证 x64 / 主版本检查 |
| WPF 界面 | 通过启动与只读搜索检查 | MineOrbit 名称、目标实例、Mods 图标和结果、34 个兼容 Mod 的真实搜索结果；未在用户实例安装测试内容 |
| 版本隔离 | 规则与配置测试通过，启动参数代码已接入 | 三种目录模式、同版本分组、共享目录冲突保护、目录占用锁；尚未通过两次真实游戏进程验收 |
| Microsoft 真人登录 / Refresh / 游戏主菜单 | **未在实际 Windows 环境验证** | 尚未收到具体获准 Client ID 与用户本人授权；代码已接入 MSAL 系统浏览器和正版会话，未用假会话启动或宣称通过 |
| CurseForge 授权导入 | 未进行授权 API 实测 | 代码仅使用官方 API、x-api-key；无 Key / 受限 URL 明确失败，不绕过限制 |
| Windows 10、全部 DPI / 全历史版本 | 未完整验证 | 只在当前 Windows 主机检查 |

## 测试覆盖

原有 22 项：实例创建/修改/隔离和保留存档的配置移除、设置、8 种危险路径、合法路径、MRPACK/CF/ZIP 识别、重复路径、ZIP Slip、未知清单、DPAPI、Hash 损坏、HTTP 失败和取消。

新增 25 项：三种隔离目录、共享 Mod 兼容性、占用锁、事务提交与失败回滚、实例复制、API Key 非明文保存、必要依赖排序/可选依赖排除、循环依赖、Loader 不兼容、固定依赖版本不兼容、声明冲突、Modrinth JSON 解析、5 类受保护路径、NeoForge 清单、未知 Loader、备份内容、共享运行资源不改变游戏隔离、Hash 缓存无网络复用、重启后未完成安装恢复及完成清理、启动器目录锁释放后仍识别活跃游戏进程。

机器报告：artifacts/test-results/phase2.trx。联网测试：tools/MineOrbit.Smoke；运行方法见 README。测试用例中的虚拟 HTTP 处理器仅用于错误处理与缓存测试；联网安装结果来自真实服务和文件，不用 Mock 成功替代验收。

## 正版启动人工验收

1. 在设置中保存获准的 Client ID，public/native 应用的回调是 http://localhost。
2. 用户本人在 Microsoft 官方系统浏览器页面授权，确认 Java Edition 权限与 Profile。
3. 对已安装 Fabric / Forge / NeoForge 实例点击启动，实际观察 Minecraft 主菜单、世界加载与退出。
4. 重启应用，验证 DPAPI 会话恢复与 Refresh；撤销授权后应明确失败。
5. 分别创建两个独立实例，确认各自 Mods 和存档不串；测试按版本分组与共享目录；切换不会删除旧目录。
6. 修改 Mods 前确认游戏目录锁生效，确认错误退出时 latest.log 与 last-output.log 可用于诊断。

## 仍待完善

真人授权到游戏启动的最后验收、CurseForge 授权实测、多平台版本兼容覆盖、Light Theme、整合包自动升级、任务排队、字节级断点续传、强制关机后的整合包恢复、自动历史缓存清理、图标编辑和物理删除 UI。

本版不将这些项目称为已完成。库部分 Loader 元数据接口没有 CancellationToken，取消可能需等待有限 HTTP 超时。禁用/删除 Mod 虽有提示，仍需用户考虑依赖；未知本地 Mod 的依赖无法可靠推断。普通 ZIP 的版本信息必须由用户声明，不能证明其中任意未知 JAR 本身兼容。自定义 Java 必须可以执行且与游戏声明主版本一致；默认官方 Runtime 仍最稳妥。
