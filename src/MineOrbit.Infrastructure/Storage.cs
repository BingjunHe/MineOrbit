using System.Text.Json;
using MineOrbit.Core;

namespace MineOrbit.Infrastructure;

public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static async Task<T?> Read<T>(string path, CancellationToken ct = default) =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct), Options) : default;
    public static async Task Write<T>(string path, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, Options), ct); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
public sealed class SettingsService
{
    // Bootstrap settings stay outside DataRoot so changing DataRoot survives restart.
    public string ConfigFile { get; }
    public SettingsService(string? configFile = null) => ConfigFile = configFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MineOrbit", "settings.json");
    public async Task<LauncherSettings> Load(CancellationToken ct = default)
    {
        var value = await JsonStore.Read<LauncherSettings>(ConfigFile, ct);
        if (value != null)
        {
            if (string.IsNullOrWhiteSpace(value.MicrosoftClientId)) value.MicrosoftClientId = LauncherIdentity.MicrosoftClientId;
            if (File.Exists(ConfigFile + ".secrets")) value.CurseForgeApiKey = new DpapiJsonStorage(ConfigFile + ".secrets").ReadAsJsonNode()?["curseforge"]?.GetValue<string>() ?? "";
            return value;
        }
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MineDeck", "settings.json");
        var migrated = await JsonStore.Read<LauncherSettings>(legacy, ct) ?? new();
        if (string.IsNullOrWhiteSpace(migrated.MicrosoftClientId)) migrated.MicrosoftClientId = LauncherIdentity.MicrosoftClientId;
        return migrated;
    }
    public async Task Save(LauncherSettings settings, CancellationToken ct = default)
    {
        settings.Validate(); await JsonStore.Write(ConfigFile, settings, ct);
        new DpapiJsonStorage(ConfigFile + ".secrets").Write(new System.Text.Json.Nodes.JsonObject { ["curseforge"] = settings.CurseForgeApiKey }, null);
    }
}
public sealed class InstanceManager
{
    private readonly SemaphoreSlim gate = new(1);
    public string Root { get; }
    public InstanceManager(string dataRoot) => Root = Path.Combine(dataRoot, "instances");
    public async Task<IReadOnlyList<GameInstance>> List(CancellationToken ct = default)
    {
        if (!Directory.Exists(Root)) return [];
        var result = new List<GameInstance>();
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var item = await JsonStore.Read<GameInstance>(Path.Combine(dir, "instance.json"), ct);
            if (item != null) { Validate(item); result.Add(item); }
        }
        return result.OrderByDescending(x => x.LastLaunched).ToList();
    }
    public GameInstance New(string name, string version, LoaderKind loader, string loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)) throw new ArgumentException("请填写游戏版本名称并选择版本。");
        var item = new GameInstance { Name = name.Trim(), MinecraftVersion = version, Loader = loader, LoaderVersion = loaderVersion, LaunchVersion = version };
        item.Directory = PathSafety.Child(Root, item.Id.ToString("N"));
        return item;
    }
    public void Validate(GameInstance item)
    {
        var expected = PathSafety.Child(Root, item.Id.ToString("N"));
        if (!string.Equals(expected, Path.GetFullPath(item.Directory), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("游戏版本路径与其 ID 不匹配。");
        var current = new DirectoryInfo(item.Directory);
        while (current != null && current.FullName.Length >= Path.GetFullPath(Root).Length)
        { if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("游戏版本路径不能包含目录链接。"); current = current.Parent; }
    }
    public async Task Save(GameInstance item, CancellationToken ct = default)
    {
        Validate(item); await gate.WaitAsync(ct);
        try { await JsonStore.Write(Path.Combine(item.Directory, "instance.json"), item, ct); var pending = Path.Combine(item.Directory, ".pending.json"); if (File.Exists(pending)) File.Delete(pending); }
        finally { gate.Release(); }
    }
    public Task SavePending(GameInstance item, CancellationToken ct) { Validate(item); return JsonStore.Write(Path.Combine(item.Directory, ".pending.json"), item, ct); }
    public async Task<IReadOnlyList<GameInstance>> Pending(CancellationToken ct = default)
    {
        if (!Directory.Exists(Root)) return [];
        var result = new List<GameInstance>();
        foreach (var folder in Directory.GetDirectories(Root))
        {
            if (File.Exists(Path.Combine(folder, "instance.json"))) continue;
            var item = await JsonStore.Read<GameInstance>(Path.Combine(folder, ".pending.json"), ct);
            if (item != null) { Validate(item); result.Add(item); }
        }
        return result;
    }
    public async Task<GameInstance> Copy(GameInstance source, CancellationToken ct, string? gameSource = null)
    {
        Validate(source);
        var copy = New(source.Name + " - 副本", source.MinecraftVersion, source.Loader, source.LoaderVersion);
        copy.LaunchVersion = source.LaunchVersion; copy.Isolation = IsolationMode.Independent;
        Directory.CreateDirectory(copy.Directory);
        async Task CopyTree(string from, string to)
        {
            GameDirectoryService.EnsureNoLinks(from); Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
            {
                ct.ThrowIfCancellationRequested();
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("不复制文件链接。");
                if (Path.GetFileName(file) == "instance.json") continue;
                await using var input = File.OpenRead(file); await using var output = File.Create(Path.Combine(to, Path.GetFileName(file))); await input.CopyToAsync(output, ct);
            }
            foreach (var dir in Directory.GetDirectories(from))
            {
                if (Path.GetFileName(dir) == ".mineorbit") continue;
                await CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
            }
        }
        await CopyTree(source.Directory, copy.Directory);
        if (gameSource != null && !string.Equals(gameSource, source.Directory, StringComparison.OrdinalIgnoreCase)) await CopyTree(gameSource, copy.Directory);
        var inventory = Path.Combine(gameSource ?? source.Directory, ".mineorbit", "mods.json");
        if (File.Exists(inventory)) { Directory.CreateDirectory(Path.Combine(copy.Directory, ".mineorbit")); File.Copy(inventory, Path.Combine(copy.Directory, ".mineorbit", "mods.json")); }
        await Save(copy, ct); return copy;
    }
    // Deliberately retain saves and all game data. Physical removal is not exposed in phase 1.
    public void RemoveConfiguration(GameInstance item) { Validate(item); File.Delete(Path.Combine(item.Directory, "instance.json")); }
}
