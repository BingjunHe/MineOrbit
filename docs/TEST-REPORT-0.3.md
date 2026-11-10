# MineOrbit 0.3 验证报告 · 2026-11-10

本机 Windows x64，.NET 10 SDK，WPF 自包含 win-x64 构建。此前 0.2 的真实 Mod / Minecraft / Loader / 整合包安装结果记录在 TEST-REPORT-0.2.md。

## 本次完成

- 所有当前 UI 页面、操作提示和错误反馈把“实例”改称“游戏版本”；磁盘 instances/<GUID> 和 JSON 字段保持兼容，不移动存档。
- 原创方块轨道图标：可编辑 SVG / WPF DrawingImage，1024px PNG 与含 16/24/32/48/64/128/256 的 Windows ICO；接入 EXE、窗口标题栏、帮助窗口与侧栏。
- 隐藏上下滚动条，仍可使用滚轮/触控滚动。横向禁用扩展宽度，保证帮助文本正常换行。
- 启动时自动刷新正版会话；没有账号或刷新明确需要交互时进入首次/重新授权，应用配置失败不会无限反复弹出登录。
- 增加 MSAL DeviceCode 登录、临时显示代码、本人打开微软验证页；系统浏览器登录仍可选。
- 显示 Microsoft / Xbox / Minecraft 验证阶段；明确启用 Java Edition 使用权检查；错误按网络、应用配置、API 拒绝、角色档案等区分，不记录原始响应或 Token。
- 内置 Client ID 注册帮助；缺少有效应用 ID 时点击启动跳转设置并显示帮助。

## 验证结果

- 全量 Release 构建通过，无编译警告/错误。
- 56 项单元测试全部通过，机器报告 artifacts/test-results/phase3.trx。
- 新增 9 项认证测试覆盖：邮箱不是应用 ID；过期会话和缺失登录提示需要授权；应用错误不循环；403/404、游戏使用权、网络与未知响应的安全提示。
- 实机已观察到 0.3 图标、游戏版本名称、隐藏的滚动条；实际发送滚轮后内容发生位移。
- 实机点击启动，应用在缺少 Client ID 时正确显示注册帮助，没有伪造登录或启动成功。
- 后续检查时检测到用户正在操作窗口，因此停止界面输入，没有自动填写或提交 Microsoft 登录页面。

## 本次无法完成的验收

用户尚未注册 MineOrbit 的 Microsoft 应用。账号邮箱不能代替 Client ID。因此真人浏览器 / 设备码授权、Token Refresh 与 Minecraft 游戏主菜单启动，仍未在实际 Windows 环境完成端到端验证。正式游戏启动管线由 CmlLib 构建真实 Java 进程；本次修复了它的首次授权衔接，但不能把代码或安装成功称为游戏已成功运行。

注册步骤见 MICROSOFT-LOGIN.md；还须确认 Minecraft API 应用准入。没有借用 PCL 的 Client ID、Token、图标或复制其登录源代码，也没有启用离线绕过。
