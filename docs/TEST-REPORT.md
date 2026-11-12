# MineOrbit 0.5.0 验证报告 · 2026-11-12

本轮增加整合包文件夹导出与官方启动器注册。历史报告见 TEST-REPORT-0.4.md 等。

## 本轮实现

- 独立标准游戏目录包含版本继承链、本体 JAR、Windows x64 所需 Libraries / natives、Hash 校验后的 Assets、旧版 virtual / resources、日志配置、游戏内容及匹配的 bundled Java。
- 不复制 MineOrbit 账号、Token、实例配置、无关缓存，不整目录复制用户配置的外部 Java。
- 同一父目录暂存，全部成功后发布。拒绝非空目标、缓存目录和链接。取消或失败保留原目标和已有游戏。
- 官方启动器注册为完整继承链及 JAR 引用分配独立 ID，兼容 Win32 / Microsoft Store profiles；保留原启动项、设置、用户名称及未知字段，精确备份原配置字节。
- gameDir 指向导出文件夹，Java 指向 bundled runtime；原 Mod / 存档不移到官方根目录。重复注册不重复建项，移动后可以更新路径。
- 依赖冲突、无效配置、启动器未关闭则中止；取消注册回滚本次新文件。界面可选择导出目录、官方目录，路径重启后保留。

## 自动测试

101 项 Release 测试全部通过：artifacts/test-results/phase5.trx。构建无警告或错误。

覆盖完整文件和必要依赖、账号排除、原配置精确备份、两个官方配置名、父版本 / jar ID 映射、重复注册、用户名称保留、移动、依赖冲突、无效 JSON、损坏 Assets、缺失本体、取消导出、取消注册回滚、运行中的启动器保护、无 Hash 的生成式运行库、x86 规则。

实际 WPF 验证导出路径绑定与重启保存，以及七页小窗口按钮和滚动；渲染图 artifacts/test-results/export-folder-0.5.png。测试使用临时配置和不激活的屏幕外窗口。

## 真实文件验证

用隔离 MRPACK 实际安装并导出 Minecraft 1.20.1、Fabric 0.20.1、Fabric API 和客户端覆盖配置，复制匹配的 Java Runtime。示例约 4064 个文件、819.6 MB。

使用 CmlLib 本地版本加载器和拒绝所有网络请求的 HTTP 客户端执行完整游戏文件检查，确认不需要补下载游戏文件。Java 通过启动项单独指定，因此此检查不请求标准目录的 Java 下载；原始 Java 在安装阶段已执行 x64 / 版本检查。

在隔离的官方测试目录合并启动项并保留既有测试配置，对新命名版本链执行相同的禁止网络检查，通过。没有伪造账号或启动游戏。

## 验证边界

未修改用户的真实官方配置或账号；尚未通过官方启动器 GUI 完成游戏主菜单验收。PCL / HMCL 标准目录指引已生成，未逐一 GUI 实测。更多 Minecraft / Loader 版本仍需要更多组合验证。

导出、注册不依赖 MineOrbit Client ID 审批。实际游戏由目标启动器正常正版认证；本轮没有重新检查 MineOrbit 的 API 审批状态。

操作步骤见 PACK-FOLDER-EXPORT.md。
