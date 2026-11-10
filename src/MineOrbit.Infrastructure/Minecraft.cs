using CmlLib.Core;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ProcessBuilder;
using MineOrbit.Core;
using CmlLib.Core.Installer.NeoForge;

namespace MineOrbit.Infrastructure;

public sealed class MinecraftVersionService
{
    public async Task<IReadOnlyList<GameVersion>> Get(CancellationToken ct) =>
        (await new MinecraftLauncher(Path.Combine(Path.GetTempPath(), "MineOrbit-version-catalog")).GetAllVersionsAsync(ct))
        .Where(x => x.Type is "release" or "snapshot").Select(x => new GameVersion(x.Name, x.Type!)).ToArray();
}
public sealed class ModLoaderInstallationService(HttpClient http)
{
    public async Task<IReadOnlyList<string>> GetVersions(string minecraft, LoaderKind kind, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (kind == LoaderKind.Vanilla) return [];
        // Upstream metadata methods lack cancellation; their HTTP client has a finite timeout.
        if (kind == LoaderKind.Fabric) return (await new FabricInstaller(http).GetLoaders(minecraft)).Select(x => x.Version!).ToArray();
        if (kind == LoaderKind.NeoForge) return (await new NeoForgeInstaller(new MinecraftLauncher(), http).GetNeoForgeVersions(minecraft)).Select(x => x.NeoForgeVersionName).ToArray();
        return (await new ForgeInstaller(new MinecraftLauncher(), http).GetForgeVersions(minecraft)).Select(x => x.ForgeVersionName).ToArray();
    }
    public async Task<string> Install(MinecraftLauncher launcher, GameInstance instance, string java, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        if (instance.Loader == LoaderKind.Vanilla) return instance.MinecraftVersion;
        if (string.IsNullOrEmpty(instance.LoaderVersion)) throw new InvalidOperationException("请先获取并选择兼容的 Loader 版本。");
        progress.Report(new("安装 " + instance.Loader));
        if (instance.Loader == LoaderKind.NeoForge)
            return await new NeoForgeInstaller(launcher, http).Install(instance.MinecraftVersion, instance.LoaderVersion, new ForgeInstallOptions { JavaPath = java, CancellationToken = ct });
        if (instance.Loader == LoaderKind.Fabric)
        {
            // Core's Fabric installer writes the official profile, then Core resolves the libraries.
            var result = await new FabricInstaller(http).Install(instance.MinecraftVersion, instance.LoaderVersion, launcher.MinecraftPath);
            ct.ThrowIfCancellationRequested(); return result;
        }
        var version = (await new ForgeInstaller(launcher, http).GetForgeVersions(instance.MinecraftVersion))
            .Single(x => x.ForgeVersionName == instance.LoaderVersion);
        // Use the public installer primitive. The high level ForgeInstaller.Install opens an ad URL.
        var installer = new ForgeInstallerVersionMapper().CreateInstaller(version);
        await installer.Install(launcher.MinecraftPath, launcher.GameInstaller, new ForgeInstallOptions
        {
            JavaPath = java, CancellationToken = ct,
            FileProgress = new Progress<CmlLib.Core.Installers.InstallerProgressChangedEventArgs>(p => progress.Report(new(p.Name ?? "Forge 文件", p.TotalTasks > 0 ? (double)p.ProgressedTasks / p.TotalTasks : null)))
        });
        return installer.VersionName;
    }
}
public sealed partial class JavaRuntimeService
{
    public string Resolve(MinecraftLauncher launcher, CmlLib.Core.Version.IVersion version, string configured)
    {
        var java = string.IsNullOrWhiteSpace(configured) ? launcher.GetJavaPath(version) ?? launcher.GetDefaultJavaPath() : configured;
        if (string.IsNullOrEmpty(java) || !File.Exists(java)) throw new InvalidOperationException("未找到适用 Java；请在设置中指定 javaw.exe，或重试自动下载。旧版本可能需要手动 Java 8。");
        return java;
    }
}
public sealed class MinecraftInstallationService(ModLoaderInstallationService loaders, JavaRuntimeService java, LauncherSettingsAccessor settings)
{
    public static MinecraftLauncher Create(string directory, IProgress<WorkProgress> progress, LauncherSettings? settings = null)
    {
        var launcher = settings == null ? new MinecraftLauncher(directory) : new MinecraftLauncher(CachePaths.Minecraft(settings, directory));
        launcher.FileProgressChanged += (_, p) => progress.Report(new($"{p.EventType} · {p.Name}", p.TotalTasks > 0 ? (double)p.ProgressedTasks / p.TotalTasks : null));
        return launcher;
    }
    public async Task Install(GameInstance instance, InstanceManager manager, IProgress<WorkProgress> progress, CancellationToken ct, bool publish = true)
    {
        manager.Validate(instance);
        using var cacheLease = CachePaths.InstallLease(settings.Value);
        Directory.CreateDirectory(instance.Directory);
        if (publish) await manager.SavePending(instance, ct);
        // Failed/cancelled installations retain verified files, but never publish instance.json.
        var launcher = Create(instance.Directory, progress, settings.Value);
        progress.Report(new("下载游戏资源、Libraries 和官方 Java Runtime"));
        await launcher.InstallAsync(instance.MinecraftVersion, ct);
        var version = await launcher.GetVersionAsync(instance.MinecraftVersion, ct);
        var runtime = java.Resolve(launcher, version, settings.Value.JavaPath);
        await java.Validate(runtime, version, ct);
        instance.LaunchVersion = await loaders.Install(launcher, instance, runtime, progress, ct);
        await launcher.GetAllVersionsAsync(ct);
        await launcher.InstallAsync(instance.LaunchVersion, ct);
        if (publish) await manager.Save(instance, ct);
        progress.Report(new("安装完成", 1));
    }
}
public sealed class MinecraftLaunchService(MicrosoftAuthenticationService auth, JavaRuntimeService java, LauncherSettingsAccessor settings, GameDirectoryService directories, MinecraftInstallationService installer)
{
    public event Action<int?>? RunningProcessChanged;
    public async Task<int> Launch(GameInstance instance, InstanceManager manager, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        manager.Validate(instance); settings.Value.Validate();
        progress.Report(new("刷新 Microsoft / Xbox / Minecraft 账号会话"));
        var session = await auth.LoginForLaunch(ct);
        await using var lease = await directories.Acquire(instance, ct);
        var gameDirectory = directories.Resolve(instance);
        FileTransactions.Recover(gameDirectory);
        if (string.IsNullOrWhiteSpace(instance.LaunchVersion) || !File.Exists(PathSafety.Child(instance.Directory, $"versions/{instance.LaunchVersion}/{instance.LaunchVersion}.json")))
        {
            progress.Report(new("启动配置缺失，正在重新安装此游戏版本和 Loader…"));
            await installer.Install(instance, manager, progress, ct);
        }
        var launcher = MinecraftInstallationService.Create(instance.Directory, progress, settings.Value);
        using (var cacheLease = CachePaths.InstallLease(settings.Value)) await launcher.InstallAsync(instance.LaunchVersion, ct);
        var version = await launcher.GetVersionAsync(instance.LaunchVersion, ct);
        var s = settings.Value;
        var runtime = java.Resolve(launcher, version, s.JavaPath);
        await java.Validate(runtime, version, ct);
        using var process = await launcher.BuildProcessAsync(instance.LaunchVersion, new MLaunchOption
        {
            Session = session, JavaPath = runtime,
            MinimumRamMb = s.MinimumRamMb, MaximumRamMb = s.MaximumRamMb,
            ScreenWidth = s.Width, ScreenHeight = s.Height,
            GameLauncherName = "MineOrbit", GameLauncherVersion = LauncherIdentity.Version, ClientId = s.MicrosoftClientId,
            ArgumentDictionary = new Dictionary<string, string> { ["game_directory"] = gameDirectory }
        }, ct);
        ct.ThrowIfCancellationRequested();
        process.StartInfo.WorkingDirectory = gameDirectory;
        var wrapper = new ProcessWrapper(process);
        // Retain the last output lines without ever persisting access tokens or the full launch arguments.
        var output = new Queue<string>();
        wrapper.OutputReceived += (_, line) =>
        {
            var safe = string.IsNullOrEmpty(session.AccessToken) ? line : line.Replace(session.AccessToken, "[REDACTED]", StringComparison.Ordinal);
            if (safe.Length > 4096) safe = safe[..4096];
            lock (output) { output.Enqueue(safe); while (output.Count > 200) output.Dequeue(); }
        };
        wrapper.StartWithEvents();
        RunningProcessChanged?.Invoke(process.Id);
        instance.LastLaunched = DateTimeOffset.Now;
        var processMarker = Path.Combine(gameDirectory, ".mineorbit", "game-process.json");
        try
        {
            await JsonStore.Write(processMarker, new ActiveGame(process.Id, process.StartTime.ToUniversalTime()), CancellationToken.None);
            await manager.Save(instance, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Serilog.Log.Warning("Game started, but launch metadata could not be saved: {Type}", ex.GetType().Name); }
        progress.Report(new("游戏运行中 · PID " + process.Id));
        // Cancellation never kills an already running game or risks losing saves.
        int exit;
        try { exit = await wrapper.WaitForExitTaskAsync(); }
        finally { RunningProcessChanged?.Invoke(null); }
        try { if (File.Exists(processMarker)) File.Delete(processMarker); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Serilog.Log.Warning("Could not remove exited-game marker: {Type}", ex.GetType().Name); }
        string[] recent; lock (output) recent = output.ToArray();
        try { await File.WriteAllLinesAsync(Path.Combine(gameDirectory, ".mineorbit", "last-output.log"), recent); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Serilog.Log.Warning("Could not persist game output: {Type}", ex.GetType().Name); }
        progress.Report(new("游戏已退出 · 退出代码 " + exit));
        return exit;
    }
}

public static class LaunchDiagnostics
{
    public static string Explain(string output)
    {
        if (output.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase) || output.Contains("Could not reserve enough space", StringComparison.OrdinalIgnoreCase)) return "Java 内存不足，请调整最大内存并关闭占用内存的软件。";
        if (output.Contains("UnsupportedClassVersionError", StringComparison.Ordinal)) return "Java 版本与游戏或 Mod 不匹配，请使用自动 Runtime 或选择正确的 x64 Java。";
        if (output.Contains("GLFW error 65542", StringComparison.OrdinalIgnoreCase) || output.Contains("Pixel format not accelerated", StringComparison.OrdinalIgnoreCase)) return "显卡驱动或 OpenGL 不满足要求，请更新显卡官方驱动。";
        if (output.Contains("Incompatible mod set", StringComparison.OrdinalIgnoreCase) || output.Contains("Missing mandatory dependencies", StringComparison.OrdinalIgnoreCase)) return "Mod 版本不兼容或缺少必要依赖，请检查当前游戏版本的 Mods。";
        return "请查看游戏日志和 .mineorbit/last-output.log，或尝试“检查并修复游戏文件”。";
    }
}
