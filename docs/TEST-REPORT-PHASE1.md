# 第一阶段验证报告 · 2026-11-10

环境：真实 Windows x64；项目内安装官方 .NET SDK 10.0.403；目标 net10.0-windows；自包含 win-x64 发布。未修改官方启动器 / PCL2 数据，未使用用户账号或现有游戏实例。

| 检查 | 结果 | 边界 |
|---|---|---|
| Release 编译 | 通过 | CmlLib API 编译验证；最终构建 0 警告、0 错误 |
| xUnit | 22 / 22 通过 | 真实本机文件、DPAPI；下载错误/取消使用隔离 HTTP 测试处理器，仅证明错误处理 |
| Mojang 版本目录 | 通过 | 实际返回 861 个版本，含 1.20.1 |
| Fabric 兼容版本列表 | 通过 | 1.20.1 返回 255 项 |
| Forge 兼容版本列表 | 通过 | 1.20.1 返回 138 项 |
| Vanilla + Java 下载 | 通过 | 真实下载游戏资源、Libraries、官方 Runtime |
| Fabric 安装 | 通过 | Minecraft 1.20.1，Fabric 0.20.1；保存实例元数据 |
| Forge 安装 | 通过 | 同一隔离测试目录复用 Vanilla 文件；1.20.1-forge-47.4.29；运行 Java 处理器并完成元数据保存 |
| Windows x64 自包含发布 | 通过 | 包含桌面 Runtime，不等同于通过所有 Windows 10/11 兼容测试 |
| WPF 实机窗口 | 通过启动及下载页操作检查 | 修复实测发现的只读属性 TwoWay 绑定与主题继承问题；真实切换下载页，点击后显示已获取 861 个版本 |
| Microsoft 真人登录、Refresh、Java 游戏进程 | **未在实际 Windows 环境验证** | 没有用户自有且获准的 Client ID 和账号授权；没有使用离线会话代替 |
| Windows 10 / 多 DPI / 全版本兼容 | 未验证 | 当前只在本机 Windows 环境检查 |
| Modrinth / 依赖解析 | 未实现 | 不宣称通过 |
| 完整整合包安装 | 未实现 | 仅测试格式识别、安全预检，未执行提取或网络依赖安装 |

22 项单元用例包含：8 种危险路径、合法嵌套路径、实例创建/修改/移除配置时保留存档、篡改实例路径、设置往返及内存校验、MRPACK/CurseForge/普通 ZIP 识别、ZIP Slip、重复路径、未知格式版本、DPAPI 非明文往返、Hash 损坏识别、HTTP 失败保留旧文件和取消清理临时下载。

机器可读报告：`artifacts/test-results/phase1.trx`。单元测试不登录、不启动 Minecraft。联网 Smoke 工具独立于单元测试，实际下载放在 `artifacts/smoke-data/instances/<GUID>`；这部分测试数据未打入发布 ZIP。

## 人工验收步骤

1. 解压发布包，在 Windows 10/11 x64 运行 MineOrbit.exe。
2. 在设置中保存数据路径、内存和自己的 Microsoft 应用 Client ID，核对应用注册回调和 Minecraft API allowlist。
3. 点击 Microsoft 登录，由用户在官方页面完成授权；确认具有 Java Edition Profile 和使用权限。
4. 从 UI 安装 1.20.1 + Fabric 或 Forge，确认真实进度、完成后实例可选。
5. 点击启动，确认 Minecraft 主菜单实际出现；进入世界再正常退出；检查退出状态、实例最近启动时间和 logs/latest.log。
6. 重启启动器后启动，验证 DPAPI 缓存恢复和 Token Refresh。撤销授权后应明确失败，重新登录后恢复。
7. 测试下载期间取消、断网再重试、内存设置错误；确认没有发布未完成的实例配置。

## 已知限制与下一步

第一阶段尚未完成端到端正版启动验收。账号认证失败会隐藏原始响应，以防 Token 泄露，诊断粒度需要后续在保密前提下细化。库部分 Loader 元数据方法没有 CancellationToken，取消可能需等待其有限 HTTP 超时。进度为当前阶段真实文件任务比率，不代表整个安装所需时间比率。

失败安装保留 GUID 目录，进程重启后尚不能恢复安装任务，只能手动清理确认无用的未登记目录。数据根目录改变不自动迁移文件。自定义 Java 路径目前只验证存在；需由用户确保版本和 x64 架构兼容。运行中的游戏不因取消而被强制结束；启动器暂时阻止任务运行时关闭窗口。

后续优先顺序：正版账号端到端验收 → Modrinth 兼容性 / 依赖解析与事务安装 → MRPACK → 完整文件 ZIP → 经授权 CurseForge → NeoForge 与体验完善。完整功能需求仍在，当前成果不是最终全功能发布。
