# MineOrbit 正版登录

0.4.0 已接入 MineOrbit 开发者提供的 Microsoft 应用 Client ID，配置为空时自动使用内置标识。通常不需要玩家自己注册应用；可在设置中替换为开发者自己的应用。Client ID 是公开应用标识，不是账号邮箱、密码或 Client Secret。

## 使用

1. 点击“登录 Microsoft”，在微软官方网页本人授权。
2. 默认使用“浏览器登录”，也可在设置选择“设备码登录”。短代码仅临时显示，不写入配置或日志。
3. 登录和启动前会自动保存并应用当前设置；有无效输入时先修正。
4. 账号通过 Microsoft、Xbox、Minecraft Java Token / Profile 及使用权验证后，才显示“已认证”。启动时自动刷新会话，需要时重新授权。
5. 账号缓存按 Client ID 存储于 LOCALAPPDATA/MineOrbit/accounts，使用 DPAPI CurrentUser 加密。不同应用 ID 不共用缓存；更换应用后重新登录。

## 当前实测：Minecraft HTTP 403

本轮已使用提供的 Client ID 完成 Microsoft 网页授权，随后 Minecraft 服务返回 HTTP 403。还未获得最终 Java 正版会话或完成游戏主菜单验证。

Minecraft Services 对第三方应用有独立准入要求。Entra 应用注册、公共客户端流和 localhost 回调设置正确，仍不代表 Client ID 获准调用 Minecraft API。首先确认 MineOrbit 应用准入，再排查账号限制和服务网络。重复注册应用、切换设备码或升级 Azure 付费服务不会自动解决 Minecraft API 准入。

在启动器中打开“登录帮助 / Minecraft API 准入说明”。也可查看认证库的注册说明及其链接到的当前 Minecraft 官方帮助：

- https://cmllib.github.io/CmlLib.Core-wiki/en/auth.microsoft/xboxauthnet.game.msal/clientid/
- https://help.minecraft.net/hc/en-us/articles/16254801392141

不要把仅有 Microsoft 授权成功当作 Minecraft 登录成功。MineOrbit 不借用其他启动器的 Client ID 或账号缓存，也不切换为离线会话。

## 自建应用（开发者可选）

1. 进入 https://entra.microsoft.com/ 的“应用注册 → 新注册”。
2. 名称 MineOrbit，账户类型需支持个人 Microsoft 账户。
3. 从概述复制“应用程序（客户端）ID”。
4. 添加“移动和桌面应用程序”，回调 http://localhost；开启“允许公共客户端流”。
5. 确认 Minecraft API 的独立准入，再在启动器设置替换应用 ID。

不要为这个桌面应用配置 Client Secret。玩家仍须拥有有效 Minecraft Java Edition 使用权限，并完成角色设置。

参考 Microsoft 桌面配置：https://learn.microsoft.com/en-us/entra/identity-platform/scenario-desktop-app-configuration 。PCL 仅作为分阶段反馈等交互参考，独立使用 MSAL / CmlLib 实现，未复制其登录代码或应用凭据。
