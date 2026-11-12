# MineOrbit 0.4.0 验证报告 · 2026-11-10

Windows x64、.NET 10、WPF。前期结果保留在 TEST-REPORT-0.3.1.md、TEST-REPORT-0.3.md、TEST-REPORT-0.2.md。

## 本轮交付

- 接入用户提供的 MineOrbit Client ID；新配置和旧空值配置均可直接使用。登录、启动、安装与搜索前保存并应用当前设置。
- Microsoft 浏览器 / 设备码登录，45 秒服务响应超时与 5 分钟交互等待，取消可传递到认证网络请求。按 Client ID 分开保存 DPAPI 加密缓存。识别失效会话、游戏使用权、档案、非 JSON HTTP 错误，反馈固定的阶段名称和数字状态，不输出响应体或 Token。
- 启动前刷新正版会话，缺失启动配置时重新安装对应 Minecraft / Loader。新增修复游戏文件按钮、真实游戏 PID 状态、内存 / Java / OpenGL / Mod 退出诊断。运行中的游戏不会被取消操作强杀；日志写入失败不改变实际退出结果。
- Modrinth / CurseForge 自动渠道，可指定单一来源；并行查询，单渠道失败时保留另一渠道结果，20 秒查询上限。CF 项目和版本使用独立标识，依赖不会串平台。同名项目不会被冒认合并。
- 下载失败时查找另一渠道中大小及共有 SHA1 / SHA512 一致的文件，最终仍验证原始 Hash。CF SHA1 元数据验证后计算本地 SHA512 用于后续文件管理。CF API 和 CDN 均发送授权 Key，CDN 重定向逐跳控制，Key 不会发给非 CF 主机。无有效 Key、作者禁止分发或不提供链接时明确失败。
- ZIP 识别外层目录、.minecraft / minecraft、单个按版本隔离的游戏目录；从唯一版本 JSON 提取版本和 Loader，无唯一结果时手动确认。支持包装目录中的 MRPACK / CurseForge 清单和常见 MultiMC / Prism 官方组件。旧账号、运行库、启动命令不导入。自定义 MultiMC 补丁 / Jar Mods、未知组件等明确拒绝。
- 蓝绿卡片界面、首页快捷入口、来源标签、中文登录选项；主页启动按钮和设置保存按钮固定在底部，保持七个导航入口、小窗口滚动、隐藏滚动条。

## 自动测试

- 87 项 Release 测试通过，artifacts/test-results/phase4.trx，无编译警告或错误。
- 覆盖多源成功 / 单源故障 / 全部故障 / 缺少 Key、固定渠道、来源标识、CF 依赖和校验值、同 Hash 下载回退、Key 主机边界。
- 覆盖带外层目录的 MRPACK 客户端覆盖优先级、CF 自定义 overrides、MultiMC 组件、完整 ZIP 的自动 Fabric 识别、外部说明文件、隔离版本目录、排除账号和旧 Runtime、多包歧义、不安全路径与可执行文件、自定义补丁拒绝。
- WPF 测试用临时配置创建实际窗口，逐页测量小尺寸布局、验证固定启动 / 保存按钮、滚轮向外层传递、输入无需移出焦点即保存、关闭和重开后保留设置。
- 检查实际 WPF 渲染：artifacts/test-results/home-0.4.png 与 small-window-settings.png；测试窗口位于屏幕外，不激活、不修改用户游戏数据。

## 真实联网验证

- 官方版本目录及 Fabric / Forge / NeoForge 列表正常响应。
- 自动模式实际搜索 Modrinth，CurseForge 未配置 Key 时清楚标注；实际安装 Iris 1.7.6 与必要依赖 Sodium 0.5.12，完成 Hash 验证、禁用、启用、重新安装和删除。只在 artifacts/smoke-data 测试目录操作。
- 带包装目录的 MRPACK 实际安装 Minecraft 1.20.1、Fabric 0.20.1 和 Fabric API，验证 client-overrides 优先。
- 带 .minecraft / 版本配置的完整 ZIP 实际自动识别并安装，验证 config 到达游戏根目录，账号配置未导入。
- MultiMC ZIP 实际读取组件、安装 Fabric 配置并提取 config，三个游戏版本仅完整成功后加入列表。

## 仍未通过的外部验收

使用用户提供的 Client ID 实际进行了 Microsoft 正版授权，流程进入 Xbox / Minecraft 阶段后 Minecraft 服务返回 HTTP 403。未取得通过认证的最终 Minecraft Java 会话，未执行游戏主菜单验收，也没有使用离线或伪造会话代替。

无凭据的服务连通性探测返回预期的 HTTP 401，说明接口可达；403 的具体应用准入或账号策略仍需由 Minecraft 服务侧确认。应首先确认新应用 Client ID 的 Minecraft API 准入。官方认证库的说明：https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game.msal/clientid/ 。仅注册 Entra 应用不保证获得该准入。

CF 没有提供授权 Key，因此 CF 实际搜索、下载及其跨平台回退只完成协议级隔离测试，未宣称通过真实授权下载。更多 Minecraft / Mod 组合、多显示器与高 DPI 仍需后续使用验证。

参考：Prism 来源选择 https://prismlauncher.org/wiki/getting-started/download-mods/ ；CF 官方接口 https://docs.curseforge.com/rest-api/ ；CF CDN 授权 https://blog.curseforge.com/introducing-api-key-authentication-for-curseforge-file-downloads/ 。未复制其他启动器的源代码、图标或应用凭据。
