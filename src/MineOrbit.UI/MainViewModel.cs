using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using MineOrbit.Core;
using MineOrbit.Infrastructure;
using Serilog;

namespace MineOrbit.UI;
public sealed class ActionCommand(Func<Task> action, Func<bool>? can = null) : ICommand
{
    public bool CanExecute(object? parameter) => can?.Invoke() ?? true;
    public async void Execute(object? parameter) => await action();
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly SettingsService store;
    private readonly LauncherSettingsAccessor settings;
    private readonly MicrosoftAuthenticationService auth;
    private readonly MinecraftVersionService versions;
    private readonly ModLoaderInstallationService loaders;
    private readonly MinecraftInstallationService install;
    private readonly MinecraftLaunchService launch;
    private readonly ModpackImportService packs;
    private InstanceManager manager;
    private CancellationTokenSource? cancellation;
    private List<GameVersion> allVersions = [];
    private GameInstance? pendingInstall;
    private string? pendingKey;
    public MainViewModel(SettingsService store, LauncherSettingsAccessor settings, MicrosoftAuthenticationService auth, MinecraftVersionService versions,
        ModLoaderInstallationService loaders, MinecraftInstallationService install, MinecraftLaunchService launch, ModpackImportService packs,
        ModCatalogService modrinth, ModDependencyResolver resolver, ModManagementService mods, GameDirectoryService directories, PackInstallationService packInstaller, BackupService backups,
        PackFolderExportService packExporter, OfficialLauncherRegistrationService officialLauncher)
    {
        this.store = store; this.settings = settings; this.auth = auth; this.versions = versions; this.loaders = loaders; this.install = install; this.launch = launch; this.packs = packs;
        manager = new(Settings.DataRoot);
        SetupLogin();
        SetupFeatures(modrinth, resolver, mods, directories, packInstaller, backups);
        SetupPackExport(packExporter, officialLauncher);
        LoginCommand = Command(async ct => { await ApplySettingsForOperation(ct); if (!EnsureLoginConfiguration()) return; await auth.Login(true, ct); Notify(nameof(Account)); SetStatus("账号已通过 Minecraft Java 认证，可以启动游戏。"); }, authError: true);
        LogoutCommand = Command(_ => { auth.Logout(); Notify(nameof(Account)); SetStatus("已清除本地账号缓存。"); return Task.CompletedTask; });
        RefreshVersionsCommand = Command(async ct => { allVersions = (await versions.Get(ct)).ToList(); FilterVersions(); SetStatus($"已获取 {allVersions.Count} 个 Minecraft 版本。"); });
        RefreshLoadersCommand = Command(async ct => { LoaderVersions.Clear(); if (SelectedVersion == null) throw new InvalidOperationException("请先选择 Minecraft 版本。"); foreach (var v in await loaders.GetVersions(SelectedVersion.Id, SelectedLoader, ct)) LoaderVersions.Add(v); LoaderVersion = LoaderVersions.FirstOrDefault(); SetStatus($"找到 {LoaderVersions.Count} 个兼容 Loader。"); });
        InstallCommand = Command(async ct =>
        {
            await ApplySettingsForOperation(ct);
            if (SelectedVersion == null) throw new InvalidOperationException("请先选择 Minecraft 版本。");
            if (SelectedLoader != LoaderKind.Vanilla && !LoaderVersions.Contains(LoaderVersion ?? "")) throw new InvalidOperationException("请获取并选择兼容 Loader 版本。");
            var key = $"{settings.Value.DataRoot}|{InstanceName}|{SelectedVersion.Id}|{SelectedLoader}|{LoaderVersion}|{NewIsolation.Mode}";
            if (pendingInstall == null || pendingKey != key)
            {
                pendingInstall = (await manager.Pending(ct)).FirstOrDefault(x => x.Name == InstanceName.Trim() && x.MinecraftVersion == SelectedVersion.Id && x.Loader == SelectedLoader && x.LoaderVersion == (LoaderVersion ?? "") && x.Isolation == NewIsolation.Mode)
                    ?? manager.New(InstanceName, SelectedVersion.Id, SelectedLoader, LoaderVersion ?? ""); pendingKey = key;
            }
            pendingInstall.Isolation = NewIsolation.Mode;
            var progress = Progress(); await Task.Run(() => install.Install(pendingInstall, manager, progress, ct), ct);
            var id = pendingInstall.Id; pendingInstall = null; await Reload(ct); Selected = Instances.First(x => x.Id == id); Page = 0;
        });
        launch.RunningProcessChanged += pid => Application.Current.Dispatcher.InvokeAsync(() => { GameProcessId = pid; Notify(nameof(GameProcessId)); Notify(nameof(IsGameRunning)); Notify(nameof(LaunchButtonLabel)); CommandManager.InvalidateRequerySuggested(); });
        LaunchCommand = Command(async ct =>
        {
            await ApplySettingsForOperation(ct); if (!EnsureLoginConfiguration()) return;
            var item = RequireSelected(); var progress = Progress();
            var exit = await Task.Run(() => launch.Launch(item, manager, progress, ct), ct);
            if (exit != 0)
            {
                var log = System.IO.Path.Combine(directories.Resolve(item), ".mineorbit", "last-output.log");
                var output = "";
                try { if (System.IO.File.Exists(log)) output = await System.IO.File.ReadAllTextAsync(log, CancellationToken.None); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
                throw new InvalidOperationException($"游戏退出代码 {exit}。" + LaunchDiagnostics.Explain(output));
            }
            Notify(nameof(Selected)); SetStatus("游戏已正常退出，存档保留在当前版本目录。");
        }, () => Selected != null);
        SaveSettingsCommand = Command(async ct => { await SaveEditedSettings(ct); SetStatus("设置已保存，重启后会保留。原目录中的游戏文件保持原位。"); });
        RenameCommand = Command(async ct => { var item = RequireSelected(); if (string.IsNullOrWhiteSpace(Rename)) throw new ArgumentException("请填写新名称。"); item.Name = Rename.Trim(); await manager.Save(item, ct); await Reload(ct); }, () => Selected != null);
        RemoveCommand = Command(async ct => { var item = RequireSelected(); if (MessageBox.Show($"从列表移除“{item.Name}”？\n仅删除 instance.json；所有存档及游戏文件保留在：\n{item.Directory}", "确认移除游戏版本配置", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { manager.RemoveConfiguration(item); await Reload(ct); } }, () => Selected != null);
        OpenInstanceCommand = Open(""); OpenModsCommand = Open("mods"); OpenLogsCommand = Open("logs");
        OpenLauncherLogsCommand = Command(_ => { OpenFolder(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MineOrbit", "logs")); return Task.CompletedTask; });
        InspectPackCommand = Command(async ct => { var dialog = new OpenFileDialog { Filter = "Minecraft 整合包|*.zip;*.mrpack" }; if (dialog.ShowDialog() == true) await InspectCore(dialog.FileName, ct); });
        CancelCommand = new ActionCommand(() => { cancellation?.Cancel(); return Task.CompletedTask; }, () => IsBusy && !IsGameRunning);
    }
    public async Task Initialize() => await Run(async ct => { var page = Math.Clamp(Settings.LastPage, 0, 6); await Reload(ct); Page = page; SetStatus($"就绪 · 已载入 {Instances.Count} 个游戏版本。"); });
    private async Task Reload(CancellationToken ct)
    {
        var selectedId = Settings.LastSelectedInstanceId;
        Instances.Clear(); foreach (var item in await manager.List(ct)) Instances.Add(item);
        PendingInstances.Clear(); foreach (var item in await manager.Pending(ct)) PendingInstances.Add(item);
        SelectedPendingInstance = null; Notify(nameof(SelectedPendingInstance));
        Selected = Instances.FirstOrDefault(x => x.Id == selectedId) ?? Instances.FirstOrDefault();
    }
    public async Task SaveEditedSettings(CancellationToken ct = default)
    {
        var snapshot = CloneSettings(Settings);
        snapshot.LastPage = Page; snapshot.LastSelectedInstanceId = Selected?.Id ?? snapshot.LastSelectedInstanceId;
        await store.Save(snapshot, ct);
        var rootChanged = !string.Equals(settings.Value.DataRoot, snapshot.DataRoot, StringComparison.OrdinalIgnoreCase);
        var authChanged = settings.Value.MicrosoftClientId != snapshot.MicrosoftClientId || settings.Value.LoginMode != snapshot.LoginMode;
        settings.Value = snapshot;
        if (rootChanged) { manager = new(snapshot.DataRoot); pendingInstall = null; pendingKey = null; await Reload(ct); }
        if (authChanged) auth.Reset();
        NewIsolation = IsolationChoices.First(x => x.Mode == snapshot.DefaultIsolation);
        Notify(nameof(Account)); Notify(nameof(InstallLocation)); Notify(nameof(ActualGameDirectory));
    }
    private async Task ApplySettingsForOperation(CancellationToken ct)
    {
        if (Application.Current.MainWindow is MainWindow { HasInvalidInput: true }) throw new ArgumentException("请先修正设置中红色标记的输入内容。");
        await SaveEditedSettings(ct);
    }
    private GameInstance RequireSelected() => Selected ?? throw new InvalidOperationException("请先选择游戏版本。");
    private ActionCommand Command(Func<CancellationToken, Task> action, Func<bool>? can = null, bool authError = false) => new(() => Run(action, authError), () => !IsBusy && (can?.Invoke() ?? true));
    private ActionCommand Open(string child) => Command(_ => { var item = RequireSelected(); manager.Validate(item); OpenFolder(System.IO.Path.Combine(directories.Resolve(item), child)); return Task.CompletedTask; }, () => Selected != null);
    private static void OpenFolder(string path) { System.IO.Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private async Task Run(Func<CancellationToken, Task> action, bool authError = false)
    {
        if (IsBusy) return;
        IsBusy = true; IsIndeterminate = true; cancellation = new();
        SetStatus("正在处理，请稍候…", false);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { SetStatus("任务已取消。已下载文件保留，可重试安装。"); }
        catch (Exception ex)
        {
            // Never record auth payloads, exception bodies or launch arguments.
            Log.Warning("Operation failed: {ExceptionType}", ex.GetType().Name);
            SetStatus(ex is AuthenticationFailureException ? ex.Message : authError ? "认证失败。请检查 Client ID、网络和正版 Java 权限。错误类型：" + ex.GetType().Name : "操作失败：" + ex.Message);
        }
        finally { cancellation.Dispose(); cancellation = null; IsBusy = false; IsIndeterminate = false; Notify(nameof(Account)); }
    }
    private IProgress<WorkProgress> Progress() => new Progress<WorkProgress>(p => { SetStatus(p.Message, false); IsIndeterminate = !p.Fraction.HasValue; Percent = Math.Clamp((p.Fraction ?? 0) * 100, 0, 100); });
    private void SetStatus(string message, bool history = true) { Status = message; if (history) { History = ($"[{DateTime.Now:HH:mm:ss}] {message}\n" + History); if (History.Length > 14000) History = History[..14000]; Log.Information("{Status}", message); } }
    public Task Inspect(string path) => Run(ct => InspectCore(path, ct));
    private async Task InspectCore(string path, CancellationToken ct)
    {
        PackPath = ""; PackResult = "正在检查压缩包结构…";
        var p = await Task.Run(() => packs.Inspect(path, ct), ct);
        PackPath = path; PackName = p.Name;
        PackMinecraft = p.Minecraft ?? ""; PackLoader = p.DetectedLoader ?? LoaderKind.Vanilla; PackLoaderVersion = p.LoaderVersion;
        Notify(nameof(PackName)); Notify(nameof(PackMinecraft)); Notify(nameof(PackLoader)); Notify(nameof(PackLoaderVersion));
        PackResult = $"{p.Kind}\nMinecraft：{p.Minecraft ?? "请手动填写"} · Loader：{p.Loader ?? "请手动选择"}\n{p.FileCount:N0} 个压缩条目 · 解压约 {p.UncompressedBytes / 1048576d:N1} MB\n{p.Notes}\n游戏内容位置：{(p.PayloadPrefix.Length == 0 ? "压缩包根目录" : p.PayloadPrefix)}";
        Page = 4; SetStatus("压缩包检查完成，确认下方版本信息后开始安装。");
    }
    private void FilterVersions() { Versions.Clear(); foreach (var item in allVersions.Where(x => IncludeSnapshots || x.Type == "release")) Versions.Add(item); SelectedVersion = Versions.FirstOrDefault(); }
    private void ResetLoaders() { LoaderVersions.Clear(); LoaderVersion = null; Notify(nameof(InstallLocation)); }
    public LauncherSettings Settings => editingSettings ??= CloneSettings(settings.Value);
    public string Account => auth.Username is { } user ? "已认证 · " + user : auth.HasSavedAccount ? "已保存 Microsoft 账号 · 启动时自动验证" : "登录 Microsoft，开启你的世界";
    public ObservableCollection<GameInstance> Instances { get; } = [];
    public ObservableCollection<GameVersion> Versions { get; } = [];
    public ObservableCollection<string> LoaderVersions { get; } = [];
    public LoaderKind[] LoaderKinds { get; } = Enum.GetValues<LoaderKind>();
    private GameInstance? selected;
    public GameInstance? Selected { get => selected; set { selected = value; if (value != null) Settings.LastSelectedInstanceId = value.Id; Rename = value?.Name ?? ""; Notify(); Notify(nameof(Rename)); OnSelectionChanged(); CommandManager.InvalidateRequerySuggested(); } }
    private GameVersion? selectedVersion;
    public GameVersion? SelectedVersion { get => selectedVersion; set { selectedVersion = value; Notify(); ResetLoaders(); } }
    private LoaderKind selectedLoader;
    public LoaderKind SelectedLoader { get => selectedLoader; set { selectedLoader = value; Notify(); ResetLoaders(); } }
    private string? loaderVersion;
    public string? LoaderVersion { get => loaderVersion; set { loaderVersion = value; Notify(); } }
    private bool snapshots;
    public bool IncludeSnapshots { get => snapshots; set { snapshots = value; Notify(); FilterVersions(); } }
    private bool busy;
    public bool IsBusy { get => busy; private set { busy = value; Notify(); Notify(nameof(CanEdit)); Notify(nameof(LaunchButtonLabel)); CommandManager.InvalidateRequerySuggested(); } }
    public int? GameProcessId { get; private set; }
    public bool IsGameRunning => GameProcessId.HasValue;
    public string LaunchButtonLabel => IsGameRunning ? "游戏运行中 · PID " + GameProcessId : IsBusy ? "正在处理…" : "▶  启动游戏";
    private int page;
    public int Page { get => page; set { page = value; Settings.LastPage = value; Notify(); Notify(nameof(IsSettingsPage)); Notify(nameof(IsHomePage)); } }
    public bool IsSettingsPage => Page == 6;
    public bool IsHomePage => Page == 0;
    public string SettingsSaveHint => "可随时保存；正常关闭窗口时会自动保存设置。";
    private string status = "就绪 · 先到设置填写 Microsoft Client ID，或直接安装游戏。";
    public string Status { get => status; private set { status = value; Notify(); } }
    private string history = "";
    public string History { get => history; private set { history = value; Notify(); } }
    private string packResult = "";
    public string PackResult { get => packResult; private set { packResult = value; Notify(); } }
    private double percent;
    public double Percent { get => percent; private set { percent = value; Notify(); } }
    private bool indeterminate;
    public bool IsIndeterminate { get => indeterminate; private set { indeterminate = value; Notify(); } }
    public string InstanceName { get; set; } = "我的世界";
    public string Rename { get; set; } = "";
    public string InstallLocation => "安装目录：" + System.IO.Path.Combine(settings.Value.DataRoot, "instances", "<独立游戏版本 ID>");
    public ICommand LoginCommand { get; } public ICommand LogoutCommand { get; } public ICommand RefreshVersionsCommand { get; }
    public ICommand RefreshLoadersCommand { get; } public ICommand InstallCommand { get; } public ICommand LaunchCommand { get; }
    public ICommand SaveSettingsCommand { get; } public ICommand RenameCommand { get; } public ICommand RemoveCommand { get; }
    public ICommand OpenInstanceCommand { get; } public ICommand OpenModsCommand { get; } public ICommand OpenLogsCommand { get; }
    public ICommand OpenLauncherLogsCommand { get; } public ICommand InspectPackCommand { get; } public ICommand CancelCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
