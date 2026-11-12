# 整合包导出为其他启动器可用的文件夹

MineOrbit 0.5.0 可把支持的 ZIP / MRPACK 安装成标准 Minecraft 游戏目录。会准备本体、Loader、必需 Libraries、Assets、Mods、配置；在可用时复制匹配的官方 Java Runtime。

## 导出步骤

1. 在“整合包”页选择 ZIP / MRPACK，检查 Minecraft、Loader、具体 Loader 版本和名称。无清单 ZIP 需补齐版本信息。
2. 在“导出到其他启动器”选择新的空文件夹，例如 D:/Minecraft/Packs/MyPack。每个整合包使用自己的文件夹。
3. 点击“将整合包导出到文件夹”，等待下载、校验和复制完成。
4. 输出包含 versions、libraries、assets、mods、config 及可用的 runtime。mineorbit-export.json 记录版本和相对 Java 路径；MineOrbit-启动说明.txt 提供使用步骤。

导出不需要 MineOrbit 的应用审批，不进行账号登录。游戏运行由目标启动器正常认证拥有 Java Edition 的账号。

## 官方 Minecraft 启动器

官方的数据目录与游戏运行目录是两个位置。仅修改“游戏目录”不会自动登记 Loader 版本。

1. 正常打开一次官方启动器，然后关闭它。
2. 在 MineOrbit 中保留或选择刚导出的文件夹。
3. 官方数据目录默认 APPDATA/.minecraft；自定义目录的用户需选择包含 launcher_profiles.json 或 launcher_profiles_microsoft_store.json 的实际位置。
4. 点击“添加到官方启动器”。程序补充必要依赖、为版本继承链分配独立 ID，备份配置并添加启动项。
5. 打开官方启动器，在 Java Edition 的“安装配置 / Installations”中启用“修改版 / Modded”筛选，选择整合包名称并使用正版账号启动。

mods、config、saves 留在导出文件夹。官方目录只补充版本和运行依赖。已有版本和同内容依赖保留；同路径依赖内容不同则中止，不覆盖原文件。原配置的精确字节备份保存在官方目录的 .mineorbit-profile-backups 下。

重复添加同一导出包会更新其路径，不重复建立启动项。移动导出文件夹后，重新选择新位置并添加。官方启动项使用期间请保留该导出文件夹。

## PCL / HMCL 等标准目录启动器

添加导出文件夹为 Minecraft 根目录，选择其中的 Loader 版本，并把实际游戏运行目录设为同一文件夹（使用根目录 / 关闭额外的版本隔离），以读取导出的 mods、config、saves。整合包之间仍通过各自独立的导出文件夹隔离。

各启动器入口和选项名称可能不同，尚未逐一进行 GUI 实测。Prism / MultiMC 自身实例格式与标准 .minecraft 目录不同，需按其导入方式处理。

## 限制与验证

- 解析范围沿用 MRPACK、CurseForge、完整 ZIP、常见 MultiMC / Prism 源包。CF 受限文件需要有效授权 Key；缺失链接会中止。
- 目标必须为空或不存在。链接、路径越界、未知组件、自定义补丁等不会被当作导出成功。
- 以 Windows x64 为目标。没有可用的 bundled Java 时，需由目标启动器下载或选择匹配的 x64 Java。
- 已真实导出 Minecraft 1.20.1 / Fabric / Fabric API，且在禁止网络请求时通过全部游戏文件完整性检查；隔离官方配置也通过相同检查。
- 当前没有在用户的实际官方启动器中完成正版登录及主菜单验收；目录完整性验证与 GUI 游戏运行验收分别记录。

参考 Fabric 官方启动器安装说明及配置格式，独立实现：
https://wiki.fabricmc.net/player:tutorials:install_mcl:windows
https://github.com/FabricMC/fabric-installer/blob/master/src/main/java/net/fabricmc/installer/client/ProfileInstaller.java
