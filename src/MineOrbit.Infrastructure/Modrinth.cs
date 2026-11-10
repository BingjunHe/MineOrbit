using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MineOrbit.Core;

namespace MineOrbit.Infrastructure;
public sealed record ModProject
{
    [JsonPropertyName("project_id")] public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    [JsonPropertyName("icon_url")] public string? Icon { get; init; }
    public string Source => Id.StartsWith("cf:", StringComparison.Ordinal) ? "CurseForge" : "Modrinth";
    public override string ToString() => $"{Title} · {Source}";
}
public sealed record ModFile(string Filename, string Url, Dictionary<string, string> Hashes, long Size, bool Primary);
public sealed record ModDependency
{
    [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
    [JsonPropertyName("version_id")] public string? VersionId { get; init; }
    [JsonPropertyName("dependency_type")] public string Type { get; init; } = "";
}
public sealed record ModVersion
{
    public string Id { get; init; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; init; } = "";
    public string Name { get; init; } = "";
    [JsonPropertyName("version_number")] public string Number { get; init; } = "";
    [JsonPropertyName("game_versions")] public string[] GameVersions { get; init; } = [];
    public string[] Loaders { get; init; } = [];
    public ModDependency[] Dependencies { get; init; } = [];
    public ModFile[] Files { get; init; } = [];
    public override string ToString() => $"{Name} · {Number}";
}
public interface IModCatalog
{
    Task<ModVersion[]> Versions(string project, GameInstance instance, CancellationToken ct);
    Task<ModVersion> Version(string id, CancellationToken ct);
    Task<JsonElement> Project(string id, CancellationToken ct) => throw new NotSupportedException("此目录不提供项目详情。");
}
public sealed class ModrinthService(HttpClient http) : IModCatalog
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static string Loader(GameInstance instance) => instance.Loader.ToString().ToLowerInvariant();
    private async Task<T> Get<T>(string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.modrinth.com/v2/" + relative);
        request.Headers.UserAgent.ParseAdd("MineOrbit/" + LauncherIdentity.Version);
        using var response = await http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidDataException("Modrinth 返回空数据。");
    }
    private sealed record SearchResult(ModProject[] Hits);
    public async Task<ModProject[]> Search(string query, GameInstance instance, CancellationToken ct)
    {
        if (instance.Loader == LoaderKind.Vanilla) throw new InvalidOperationException("Vanilla 不支持安装 Loader Mods；请先创建 Fabric / Forge / NeoForge 游戏版本。");
        var facets = JsonSerializer.Serialize(new[] { new[] { "project_type:mod" }, new[] { "versions:" + instance.MinecraftVersion }, new[] { "categories:" + Loader(instance) } });
        return (await Get<SearchResult>($"search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}&limit=40", ct)).Hits;
    }
    public Task<ModVersion[]> Versions(string project, GameInstance instance, CancellationToken ct) => Get<ModVersion[]>($"project/{Uri.EscapeDataString(project)}/version?loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { Loader(instance) }))}&game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { instance.MinecraftVersion }))}", ct);
    public Task<ModVersion> Version(string id, CancellationToken ct) => Get<ModVersion>("version/" + Uri.EscapeDataString(id), ct);
    public Task<JsonElement> Project(string id, CancellationToken ct) => Get<JsonElement>("project/" + Uri.EscapeDataString(id), ct);
    public Task<ModVersion> FromHash(string hash, string algorithm, CancellationToken ct) => Get<ModVersion>($"version_file/{Uri.EscapeDataString(hash)}?algorithm={algorithm}", ct);
}
public sealed class ModDependencyResolver(IModCatalog catalog)
{
    public static void Check(ModVersion version, GameInstance instance)
    {
        if (instance.Loader == LoaderKind.Vanilla || !version.GameVersions.Contains(instance.MinecraftVersion) || !version.Loaders.Contains(ModrinthService.Loader(instance)))
            throw new InvalidDataException($"Mod {version.Name} 与目标 Minecraft / Loader 不兼容。");
    }
    public async Task<IReadOnlyList<ModVersion>> Resolve(ModVersion selected, GameInstance instance, CancellationToken ct)
    {
        var resolved = new Dictionary<string, ModVersion>(); var visiting = new HashSet<string>(); var result = new List<ModVersion>();
        async Task Visit(ModVersion version)
        {
            ct.ThrowIfCancellationRequested(); Check(version, instance);
            if (resolved.Count + visiting.Count >= 128) throw new InvalidDataException("依赖数量超出安全限制。");
            if (resolved.TryGetValue(version.ProjectId, out var previous))
            { if (previous.Id != version.Id) throw new InvalidDataException("依赖要求同一 Mod 的不同版本，请手动调整。"); return; }
            if (!visiting.Add(version.Id)) throw new InvalidDataException("发现循环依赖，无法自动确定安装顺序。");
            foreach (var dependency in version.Dependencies.Where(d => d.Type == "required"))
            {
                var item = dependency.VersionId != null ? await catalog.Version(dependency.VersionId, ct)
                    : dependency.ProjectId != null ? (await catalog.Versions(dependency.ProjectId, instance, ct)).FirstOrDefault()
                    : throw new InvalidDataException("依赖未声明可识别的项目或版本。");
                if (item == null) throw new InvalidDataException("没有找到兼容的必要依赖。");
                await Visit(item);
            }
            visiting.Remove(version.Id); resolved.Add(version.ProjectId, version); result.Add(version);
        }
        await Visit(selected);
        foreach (var version in result)
            foreach (var incompatible in version.Dependencies.Where(d => d.Type == "incompatible"))
                if (result.Any(v => (incompatible.VersionId != null && v.Id == incompatible.VersionId) || (incompatible.VersionId == null && v.ProjectId == incompatible.ProjectId)))
                    throw new InvalidDataException("所选 Mod 与依赖中的另一个 Mod 不兼容。");
        return result;
    }
}
public sealed record InstalledMod(string ProjectId, string VersionId, string Name, string Filename, string Sha512, bool Disabled = false)
{ public bool IsLocal => ProjectId.StartsWith("local:", StringComparison.Ordinal); public override string ToString() => $"{(Disabled ? "[已禁用] " : "")}{Name} · {Filename}"; }
public sealed class ModManagementService(IModCatalog catalog, ModDependencyResolver resolver, DownloadManager downloads, GameDirectoryService directories)
{
    private static string Inventory(string root) => Path.Combine(root, ".mineorbit", "mods.json");
    public async Task<List<InstalledMod>> List(GameInstance instance, CancellationToken ct)
    {
        await using var lease = await directories.Acquire(instance, ct); var root = directories.Resolve(instance); FileTransactions.Recover(root);
        var items = await JsonStore.Read<List<InstalledMod>>(Inventory(root), ct) ?? [];
        var mods = Path.Combine(root, "mods");
        if (Directory.Exists(mods))
        {
            GameDirectoryService.EnsureNoLinks(mods);
            foreach (var file in Directory.GetFiles(mods).Where(x => x.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(file); var disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase); var filename = disabled ? name[..^9] : name;
                if (items.Any(x => x.Filename.Equals(filename, StringComparison.OrdinalIgnoreCase))) continue;
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Mods 目录包含文件链接。");
                await using var stream = File.OpenRead(file); var hash = Convert.ToHexString(await System.Security.Cryptography.SHA512.HashDataAsync(stream, ct));
                items.Add(new("local:" + filename, "", "本地文件", filename, hash, disabled));
            }
        }
        return items;
    }
    public async Task Install(GameInstance instance, ModVersion selected, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        var plan = await resolver.Resolve(selected, instance, ct);
        await using var lease = await directories.Acquire(instance, ct); var root = directories.Resolve(instance); FileTransactions.Recover(root);
        var installed = await JsonStore.Read<List<InstalledMod>>(Inventory(root), ct) ?? [];
        foreach (var existing in installed.Where(x => !x.Disabled && !x.IsLocal && !plan.Any(p => p.ProjectId == x.ProjectId)))
        {
            var version = await catalog.Version(existing.VersionId, ct);
            foreach (var dependency in version.Dependencies)
            {
                if (dependency.Type == "incompatible" && plan.Any(p => dependency.VersionId != null ? p.Id == dependency.VersionId : p.ProjectId == dependency.ProjectId))
                    throw new InvalidDataException("已有 Mod 声明与本次安装计划不兼容：" + existing.Name);
                if (dependency.Type == "required" && dependency.VersionId != null)
                {
                    var required = await catalog.Version(dependency.VersionId, ct);
                    if (plan.Any(p => p.ProjectId == required.ProjectId && p.Id != required.Id)) throw new InvalidDataException("本次更新会破坏已有 Mod 的固定版本依赖：" + existing.Name);
                }
            }
        }
        foreach (var version in plan)
            foreach (var d in version.Dependencies.Where(x => x.Type == "incompatible"))
                if (installed.Any(x => !x.Disabled && (d.VersionId != null ? x.VersionId == d.VersionId : x.ProjectId == d.ProjectId)))
                    throw new InvalidDataException("目标游戏版本存在声明不兼容的 Mod，请先处理。");
        var stage = Path.Combine(root, ".mineorbit", "stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            var changes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var version in plan)
            {
                var project = await catalog.Project(version.ProjectId, ct);
                if (project.TryGetProperty("client_side", out var clientSide) && clientSide.GetString() == "unsupported") throw new InvalidDataException("拒绝安装仅服务端 Mod。");
                if (version.Files.Length == 0) throw new InvalidDataException("作者未提供此文件的第三方下载链接，请通过项目官方页面获取。");
                var file = version.Files.SingleOrDefault(x => x.Primary) ?? (version.Files.Length == 1 ? version.Files[0] : throw new InvalidDataException("无法可靠选择 Mod 主文件。"));
                if (!file.Filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file.Filename) != file.Filename) throw new InvalidDataException("Mod 文件名不安全。");
                var algorithm = file.Hashes.ContainsKey("sha512") ? "sha512" : "sha1";
                if (!file.Hashes.TryGetValue(algorithm, out var hash) || file.Size is <= 0 or > 536870912) throw new InvalidDataException("Mod 缺少 Hash 或文件过大。");
                var old = installed.SingleOrDefault(x => x.ProjectId == version.ProjectId);
                var relative = "mods/" + file.Filename;
                if (changes.ContainsKey(relative)) throw new InvalidDataException("两个项目使用相同文件名。");
                var target = PathSafety.Child(root, relative);
                if (File.Exists(target) && (old == null || old.Filename != file.Filename)) throw new InvalidDataException("目标文件已存在且不属于当前管理项目，不会覆盖。");
                progress.Report(new("下载 Mod 和必要依赖 · " + version.Name));
                var staged = Path.Combine(stage, Guid.NewGuid().ToString("N"));
                if (catalog is ModCatalogService multi) await multi.Download(version, file, instance, staged, downloads, progress, ct);
                else await downloads.DownloadHash(new Uri(file.Url), staged, hash, algorithm, file.Size, progress, ct);
                if (new FileInfo(staged).Length != file.Size) throw new InvalidDataException("Mod 文件大小与清单不符。");
                await using (var verified = File.OpenRead(staged)) hash = Convert.ToHexString(await System.Security.Cryptography.SHA512.HashDataAsync(verified, ct));
                changes[relative] = staged;
                if (old != null)
                {
                    var oldRelative = "mods/" + old.Filename + (old.Disabled ? ".disabled" : "");
                    var oldPath = PathSafety.Child(root, oldRelative);
                    if (File.Exists(oldPath)) await DownloadManager.Verify(oldPath, old.Sha512, ct);
                    if (oldRelative != relative) changes[oldRelative] = null;
                    installed.Remove(old);
                }
                installed.Add(new(version.ProjectId, version.Id, version.Name, file.Filename, hash));
            }
            var inventory = Path.Combine(stage, "inventory.json"); await JsonStore.Write(inventory, installed, ct); changes[".mineorbit/mods.json"] = inventory;
            await FileTransactions.Commit(root, changes, ct);
            progress.Report(new($"已安装 {plan.Count} 个 Mod（含必要依赖）", 1));
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
    public async Task RemoveOrToggle(GameInstance instance, InstalledMod item, bool remove, CancellationToken ct)
    {
        await using var lease = await directories.Acquire(instance, ct); var root = directories.Resolve(instance); FileTransactions.Recover(root);
        var installed = await JsonStore.Read<List<InstalledMod>>(Inventory(root), ct) ?? [];
        var current = installed.SingleOrDefault(x => x.ProjectId == item.ProjectId) ?? (item.IsLocal ? item : throw new InvalidDataException("Mod 已发生变化，请刷新列表。"));
        var stage = Path.Combine(root, ".mineorbit", "edit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            var relative = "mods/" + current.Filename + (current.Disabled ? ".disabled" : "");
            var source = PathSafety.Child(root, relative);
            if (File.Exists(source)) await DownloadManager.Verify(source, current.Sha512, ct);
            var changes = new Dictionary<string, string?> { [relative] = null };
            installed.Remove(current);
            if (!remove)
            {
                if (!File.Exists(source)) throw new FileNotFoundException("Mod 文件已被移除，请刷新或重新安装。");
                var next = "mods/" + current.Filename + (current.Disabled ? "" : ".disabled");
                if (File.Exists(PathSafety.Child(root, next))) throw new IOException("目标名称已存在。");
                var staged = Path.Combine(stage, "mod.jar"); File.Copy(source, staged); changes[next] = staged;
                installed.Add(current with { Disabled = !current.Disabled });
            }
            var json = Path.Combine(stage, "inventory.json"); await JsonStore.Write(json, installed, ct); changes[".mineorbit/mods.json"] = json;
            await FileTransactions.Commit(root, changes, ct);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }
}
