namespace MineOrbit.Core;

public enum LoaderKind { Vanilla, Fabric, Forge, NeoForge }
public enum IsolationMode { Independent, ByVersion, Shared }
public enum MicrosoftLoginMode { SystemBrowser, DeviceCode }
public enum ModDownloadSource { Automatic, Modrinth, CurseForge }
public static class LauncherIdentity
{
    public const string Version = "0.4.0";
    public const string MicrosoftClientId = "9be6cf9b-cacb-4201-ab57-dc3847b61587";
}
public record IsolationChoice(IsolationMode Mode, string Label) { public override string ToString() => Label; }
public sealed record GameInstance
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "我的 Minecraft";
    public string MinecraftVersion { get; init; } = "";
    public LoaderKind Loader { get; init; }
    public string LoaderVersion { get; init; } = "";
    public string LaunchVersion { get; set; } = "";
    public string Directory { get; set; } = "";
    public DateTimeOffset? LastLaunched { get; set; }
    public IsolationMode Isolation { get; set; } = IsolationMode.Independent;
    public override string ToString() => $"{Name}   ·   {MinecraftVersion} / {Loader} {LoaderVersion}";
}
public sealed class LauncherSettings
{
    public string DataRoot { get; set; } = @"D:\Minecraft\MineOrbit";
    public string JavaPath { get; set; } = "";
    public string MicrosoftClientId { get; set; } = LauncherIdentity.MicrosoftClientId;
    public MicrosoftLoginMode LoginMode { get; set; } = MicrosoftLoginMode.SystemBrowser;
    public int MinimumRamMb { get; set; } = 1024;
    public int MaximumRamMb { get; set; } = 4096;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public IsolationMode DefaultIsolation { get; set; } = IsolationMode.Independent;
    public int DownloadConcurrency { get; set; } = 4;
    public bool ShareRuntimeAndAssets { get; set; } = true;
    public ModDownloadSource ModDownloadSource { get; set; } = ModDownloadSource.Automatic;
    public string LibrariesPath { get; set; } = "";
    public string AssetsPath { get; set; } = "";
    public string RuntimePath { get; set; } = "";
    public string DownloadCachePath { get; set; } = "";
    public Guid? LastSelectedInstanceId { get; set; }
    public int LastPage { get; set; }
    public double LauncherWindowWidth { get; set; } = 1180;
    public double LauncherWindowHeight { get; set; } = 800;
    [System.Text.Json.Serialization.JsonIgnore] public string CurseForgeApiKey { get; set; } = "";
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(DataRoot)) throw new ArgumentException("游戏数据目录必须为绝对路径。");
        if (MinimumRamMb < 512 || MaximumRamMb < MinimumRamMb || MaximumRamMb > 131072)
            throw new ArgumentException("内存范围无效：最小值至少 512 MB，且不能大于最大值。");
        if (Width < 320 || Height < 240) throw new ArgumentException("游戏窗口尺寸过小。");
        if (DownloadConcurrency is < 1 or > 16) throw new ArgumentException("下载并发范围为 1–16。");
        foreach (var path in new[] { LibrariesPath, AssetsPath, RuntimePath, DownloadCachePath })
            if (path.Length > 0 && !Path.IsPathFullyQualified(path)) throw new ArgumentException("自定义缓存目录必须为绝对路径。");
        if (JavaPath.Length > 0 && !File.Exists(JavaPath)) throw new ArgumentException("指定的 Java 文件不存在。");
    }
}
public record GameVersion(string Id, string Type) { public override string ToString() => $"{Id}  ({Type})"; }
public record WorkProgress(string Message, double? Fraction = null);

public static class PathSafety
{
    public static string Child(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("不安全的相对路径。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径越界。");
        // Reject Windows aliases and alternate streams even on a non-Windows test host.
        foreach (var part in relative.Replace('\\', '/').Split('/'))
        {
            if (part is ".." or "." || part.EndsWith('.') || part.EndsWith(' ')) throw new InvalidDataException("路径包含别名。");
            var name = part.Split('.')[0].ToUpperInvariant();
            if (name is "CON" or "PRN" or "AUX" or "NUL" || (name.Length == 4 && (name.StartsWith("COM") || name.StartsWith("LPT")) && char.IsDigit(name[3])))
                throw new InvalidDataException("路径包含设备名称。");
        }
        return target;
    }
}
