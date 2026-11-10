using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using MineOrbit.Core;
using MineOrbit.Infrastructure;

namespace MineOrbit.UI;
public sealed partial class MainViewModel
{
    private ModCatalogService modrinth = null!;
    private ModDependencyResolver resolver = null!;
    private ModManagementService mods = null!;
    private GameDirectoryService directories = null!;
    private PackInstallationService packInstaller = null!;
    private BackupService backups = null!;
    private LauncherSettings? editingSettings;
    private static LauncherSettings CloneSettings(LauncherSettings source)
    { var clone = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(source))!; clone.CurseForgeApiKey = source.CurseForgeApiKey; return clone; }
    public bool CanEdit => !IsBusy;
    public IsolationChoice[] IsolationChoices { get; } = [new(IsolationMode.Independent, "独立游戏版本隔离（推荐）"), new(IsolationMode.ByVersion, "按 Minecraft + Loader 版本隔离"), new(IsolationMode.Shared, "共享游戏目录（不隔离）")];
    private IsolationChoice newIsolation = new(IsolationMode.Independent, "独立游戏版本隔离（推荐）");
    public IsolationChoice NewIsolation { get => newIsolation; set { newIsolation = value; Notify(); } }
    private IsolationChoice currentIsolation = new(IsolationMode.Independent, "独立游戏版本隔离（推荐）");
    public IsolationChoice CurrentIsolation { get => currentIsolation; set { currentIsolation = value; Notify(); } }
    public string ActualGameDirectory => Selected == null ? "未选择游戏版本" : directories.Resolve(Selected);
    public string IsolationDescription => Selected == null ? "" : IsolationChoices.First(x => x.Mode == Selected.Isolation).Label;
    public ObservableCollection<ModProject> ModResults { get; } = [];
    public ObservableCollection<ModVersion> ModVersions { get; } = [];
    public ObservableCollection<InstalledMod> InstalledMods { get; } = [];
    public ObservableCollection<GameInstance> PendingInstances { get; } = [];
    public ObservableCollection<JavaCandidate> JavaCandidates { get; } = [];
    private JavaCandidate? selectedJavaCandidate;
    public JavaCandidate? SelectedJavaCandidate
    {
        get => selectedJavaCandidate;
        set { selectedJavaCandidate = value; Notify(); if (value != null) { Settings.JavaPath = value.Path; Notify(nameof(Settings)); } }
    }
    public GameInstance? SelectedPendingInstance { get; set; }
    public string ModQuery { get; set; } = "";
    public ModSourceChoice[] ModSources { get; } = [new(ModDownloadSource.Automatic, "自动选择可用渠道"), new(ModDownloadSource.Modrinth, "仅 Modrinth"), new(ModDownloadSource.CurseForge, "仅 CurseForge")];
    public ModDownloadSource SelectedModSource { get => Settings.ModDownloadSource; set { Settings.ModDownloadSource = value; Notify(); Notify(nameof(ModChannelHint)); } }
    public string ModChannelHint => SelectedModSource == ModDownloadSource.Automatic ? "自动查询 Modrinth 和已配置的 CurseForge；渠道故障时保留其他结果，下载失败时匹配相同文件。" : "当前只使用所选渠道。CurseForge 需要授权的 API Key。";
    private ModProject? modProject;
    public ModProject? SelectedModProject { get => modProject; set { modProject = value; ModVersions.Clear(); SelectedModVersion = null; Notify(); Notify(nameof(ModDescription)); } }
    public string ModDescription => SelectedModProject?.Description ?? "搜索结果会按目标游戏版本的 Minecraft 和 Loader 自动筛选。";
    public ModVersion? SelectedModVersion { get; set; }
    public InstalledMod? SelectedInstalledMod { get; set; }
    private string packPath = "";
    public string PackPath { get => packPath; set { packPath = value; Notify(); } }
    public string PackName { get; set; } = "我的整合包";
    public string PackMinecraft { get; set; } = "";
    public LoaderKind PackLoader { get; set; }
    public string PackLoaderVersion { get; set; } = "";
    public bool IncludeOptionalPackFiles { get; set; }
    public ICommand SearchModsCommand { get; private set; } = null!;
    public ICommand GetModVersionsCommand { get; private set; } = null!;
    public ICommand InstallModCommand { get; private set; } = null!;
    public ICommand RefreshModsCommand { get; private set; } = null!;
    public ICommand UpdateModCommand { get; private set; } = null!;
    public ICommand RemoveModCommand { get; private set; } = null!;
    public ICommand ToggleModCommand { get; private set; } = null!;
    public ICommand ImportPackCommand { get; private set; } = null!;
    public ICommand ApplyIsolationCommand { get; private set; } = null!;
    public ICommand CopyInstanceCommand { get; private set; } = null!;
    public ICommand BackupCommand { get; private set; } = null!;
    public ICommand RefreshPendingCommand { get; private set; } = null!;
    public ICommand ResumeInstallCommand { get; private set; } = null!;
    public ICommand DetectJavaCommand { get; private set; } = null!;
    public ICommand RepairGameCommand { get; private set; } = null!;
    public ICommand GoDownloadCommand => new ActionCommand(() => { Page = 2; return Task.CompletedTask; });
    public ICommand GoModsCommand => new ActionCommand(() => { Page = 3; return Task.CompletedTask; });
    public ICommand GoImportCommand => new ActionCommand(() => { Page = 4; return Task.CompletedTask; });
    private void OnSelectionChanged()
    {
        CurrentIsolation = IsolationChoices.First(x => x.Mode == (Selected?.Isolation ?? IsolationMode.Independent));
        ModResults.Clear(); ModVersions.Clear(); InstalledMods.Clear(); SelectedModProject = null; SelectedInstalledMod = null;
        Notify(nameof(ActualGameDirectory)); Notify(nameof(IsolationDescription));
    }
    private void SetupFeatures(ModCatalogService catalog, ModDependencyResolver dependencyResolver, ModManagementService management, GameDirectoryService gameDirectories, PackInstallationService imports, BackupService backupService)
    {
        modrinth = catalog; resolver = dependencyResolver; mods = management; directories = gameDirectories; packInstaller = imports; backups = backupService;
        NewIsolation = IsolationChoices.First(x => x.Mode == Settings.DefaultIsolation);
        RepairGameCommand = Command(async ct => { await ApplySettingsForOperation(ct); var item = RequireSelected(); await using var lease = await directories.Acquire(item, ct); var progress = Progress(); await Task.Run(() => install.Install(item, manager, progress, ct), ct); SetStatus("游戏本体和 Loader 已检查并修复，Mods 与存档保留。"); }, () => Selected != null);
        DetectJavaCommand = Command(async ct => { JavaCandidates.Clear(); foreach (var java in await new JavaRuntimeService().Detect(ct)) JavaCandidates.Add(java); SetStatus($"检测到 {JavaCandidates.Count} 个本机 Java；可从列表选择，留空仍使用自动 Runtime。"); });
        SearchModsCommand = Command(async ct => { await ApplySettingsForOperation(ct); var item = RequireSelected(); ModResults.Clear(); var results = await modrinth.Search(ModQuery, item, ct); foreach (var result in results.Projects) ModResults.Add(result); SetStatus($"找到 {ModResults.Count} 个兼容 Mod · {results.Note}"); }, () => Selected != null);
        GetModVersionsCommand = Command(async ct => { ModVersions.Clear(); foreach (var version in await modrinth.Versions(SelectedModProject!.Id, RequireSelected(), ct)) ModVersions.Add(version); SelectedModVersion = ModVersions.FirstOrDefault(); Notify(nameof(SelectedModVersion)); }, () => Selected != null && SelectedModProject != null);
        InstallModCommand = Command(async ct => await ConfirmInstall(RequireSelected(), SelectedModVersion!, ct), () => Selected != null && SelectedModVersion != null);
        RefreshModsCommand = Command(async ct => await ReloadMods(RequireSelected(), ct), () => Selected != null);
        UpdateModCommand = Command(async ct =>
        {
            var item = RequireSelected(); var existing = SelectedInstalledMod!;
            if (existing.IsLocal) throw new InvalidOperationException("本地未知来源 Mod 无法可靠自动更新，请从搜索结果选择对应项目。");
            var latest = (await modrinth.Versions(existing.ProjectId, item, ct)).FirstOrDefault() ?? throw new InvalidOperationException("没有兼容更新。");
            if (latest.Id == existing.VersionId) { SetStatus("此 Mod 已是当前兼容版本。"); return; }
            await ConfirmInstall(item, latest, ct);
        }, () => Selected != null && SelectedInstalledMod != null);
        RemoveModCommand = Command(async ct =>
        {
            if (MessageBox.Show($"移除 {SelectedInstalledMod!.Name}？\n如果其他 Mod 依赖它，游戏可能无法加载。建议先备份。", "确认删除 Mod", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var item = RequireSelected(); await mods.RemoveOrToggle(item, SelectedInstalledMod, true, ct); await ReloadMods(item, ct); SetStatus("Mod 已移除。");
        }, () => Selected != null && SelectedInstalledMod != null);
        ToggleModCommand = Command(async ct => { var item = RequireSelected(); await mods.RemoveOrToggle(item, SelectedInstalledMod!, false, ct); await ReloadMods(item, ct); SetStatus("已切换 Mod 启用状态；依赖它的 Mod 可能需要一并处理。"); }, () => Selected != null && SelectedInstalledMod != null);
        ImportPackCommand = Command(async ct =>
        {
            await ApplySettingsForOperation(ct);
            if (!System.IO.File.Exists(PackPath)) throw new InvalidOperationException("请先选择整合包文件。");
            var progress = Progress(); var options = new PackImportOptions(PackName, PackMinecraft, PackLoader, PackLoaderVersion, IncludeOptionalPackFiles);
            var created = await Task.Run(() => packInstaller.Import(PackPath, options, manager, progress, ct), ct);
            await Reload(ct); Selected = Instances.First(x => x.Id == created.Id); Page = 0; SetStatus("整合包已安装为独立游戏版本，可使用正版账号启动。");
        });
        ApplyIsolationCommand = Command(async ct =>
        {
            var item = RequireSelected(); var old = item.Isolation;
            if (MessageBox.Show("更改隔离模式后，游戏会使用新的目录。\n不会搬迁或删除原有 Mods、设置和存档；可切回原模式。\n共享目录会限制不兼容 Mods 混装。是否应用？", "版本隔离", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            item.Isolation = CurrentIsolation.Mode;
            try { await using var lease = await directories.Acquire(item, ct); await manager.Save(item, ct); }
            catch { item.Isolation = old; throw; }
            OnSelectionChanged(); SetStatus("隔离模式已保存。游戏目录：" + ActualGameDirectory);
        }, () => Selected != null);
        CopyInstanceCommand = Command(async ct =>
        {
            var item = RequireSelected(); await using var lease = await directories.Acquire(item, ct);
            var gameSource = directories.Resolve(item); var copy = await Task.Run(() => manager.Copy(item, ct, gameSource), ct);
            await Reload(ct); Selected = Instances.First(x => x.Id == copy.Id); SetStatus("已复制为独立游戏版本。");
        }, () => Selected != null);
        BackupCommand = Command(async ct => { var item = RequireSelected(); var file = await Task.Run(() => backups.Create(item, ct), ct); SetStatus("备份完成：" + file); }, () => Selected != null);
        RefreshPendingCommand = Command(async ct => { PendingInstances.Clear(); foreach (var item in await manager.Pending(ct)) PendingInstances.Add(item); SetStatus($"找到 {PendingInstances.Count} 个未完成安装。"); });
        ResumeInstallCommand = Command(async ct =>
        {
            var item = SelectedPendingInstance!; var progress = Progress();
            await Task.Run(() => install.Install(item, manager, progress, ct), ct);
            await Reload(ct); Selected = Instances.First(x => x.Id == item.Id); SetStatus("未完成安装已恢复并完成。");
        }, () => SelectedPendingInstance != null);
    }
    private async Task ReloadMods(GameInstance item, CancellationToken ct)
    { InstalledMods.Clear(); foreach (var installed in await mods.List(item, ct)) InstalledMods.Add(installed); SelectedInstalledMod = InstalledMods.FirstOrDefault(); Notify(nameof(SelectedInstalledMod)); }
    private async Task ConfirmInstall(GameInstance item, ModVersion selectedVersion, CancellationToken ct)
    {
        var plan = await resolver.Resolve(selectedVersion, item, ct);
        var message = $"目标：{item.Name}\nMinecraft {item.MinecraftVersion} / {item.Loader}\n目录：{directories.Resolve(item)}\n\n将安装或更新以下 Mod（含必要依赖）：\n" + string.Join("\n", plan.Select(x => x.Name + " " + x.Number)) + "\n\n是否继续？";
        if (MessageBox.Show(message, "确认 Mod 安装计划", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var progress = Progress(); await Task.Run(() => mods.Install(item, selectedVersion, progress, ct), ct); await ReloadMods(item, ct); SetStatus("Mod 和必要依赖已安装，可重新启动游戏。");
    }
}
