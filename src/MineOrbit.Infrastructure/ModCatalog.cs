using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MineOrbit.Core;

namespace MineOrbit.Infrastructure;

public sealed class CurseForgeService(HttpClient http, LauncherSettingsAccessor settings)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Value.CurseForgeApiKey);
    public static int LoaderId(LoaderKind kind) => kind switch { LoaderKind.Forge => 1, LoaderKind.Fabric => 4, LoaderKind.NeoForge => 6, _ => throw new InvalidOperationException("原版游戏不能安装 Loader Mod。") };
    private async Task<JsonElement> Get(string path, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("请先在设置中填写授权的 CurseForge API Key；自动模式仍可使用 Modrinth。");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.curseforge.com/v1/" + path);
        request.Headers.Add("x-api-key", settings.Value.CurseForgeApiKey);
        request.Headers.UserAgent.ParseAdd("MineOrbit/" + LauncherIdentity.Version);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new HttpRequestException("CurseForge Key 未获授权或已失效。", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.GetProperty("data").Clone();
    }
    public static int ProjectId(string value)
    {
        if (!value.StartsWith("cf:", StringComparison.Ordinal) || !int.TryParse(value.AsSpan(3), out var id) || id <= 0) throw new InvalidDataException("CurseForge 项目标识无效。");
        return id;
    }
    public async Task<ModProject[]> Search(string query, GameInstance instance, CancellationToken ct)
    {
        var data = await Get($"mods/search?gameId=432&classId=6&searchFilter={Uri.EscapeDataString(query)}&gameVersion={Uri.EscapeDataString(instance.MinecraftVersion)}&modLoaderType={LoaderId(instance.Loader)}&sortField=2&sortOrder=desc&pageSize=40", ct);
        return data.EnumerateArray().Select(x => new ModProject
        {
            Id = "cf:" + x.GetProperty("id").GetInt32(), Title = x.GetProperty("name").GetString() ?? "",
            Description = x.GetProperty("summary").GetString() ?? "",
            Icon = x.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object ? logo.GetProperty("thumbnailUrl").GetString() : null
        }).ToArray();
    }
    public async Task<ModVersion[]> Versions(string project, GameInstance instance, CancellationToken ct)
    {
        var data = await Get($"mods/{ProjectId(project)}/files?gameVersion={Uri.EscapeDataString(instance.MinecraftVersion)}&modLoaderType={LoaderId(instance.Loader)}&pageSize=50", ct);
        return data.EnumerateArray().OrderByDescending(x => x.GetProperty("fileDate").GetString()).Select(MapVersion)
            .Where(x => x.GameVersions.Contains(instance.MinecraftVersion) && x.Loaders.Contains(ModrinthService.Loader(instance))).ToArray();
    }
    public async Task<ModVersion> Version(string id, CancellationToken ct)
    {
        var split = id.Split('/');
        if (split.Length != 2 || !int.TryParse(split[1], out var file) || file <= 0) throw new InvalidDataException("CurseForge 文件标识无效。");
        var project = ProjectId(split[0]);
        var result = MapVersion(await Get($"mods/{project}/files/{file}", ct));
        if (result.ProjectId != split[0] || result.Id != id) throw new InvalidDataException("CurseForge 返回了其他项目或文件。");
        return result;
    }
    public async Task<JsonElement> Project(string id, CancellationToken ct)
    {
        var project = await Get($"mods/{ProjectId(id)}", ct);
        if (project.TryGetProperty("allowModDistribution", out var allowed) && allowed.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException("此项目作者禁止第三方分发，请通过 CurseForge 官方页面获取。");
        return JsonSerializer.SerializeToElement(new { client_side = "optional", slug = project.GetProperty("slug").GetString() });
    }
    public static ModVersion MapVersion(JsonElement file)
    {
        var project = "cf:" + file.GetProperty("modId").GetInt32();
        var games = file.GetProperty("gameVersions").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var hashes = new Dictionary<string, string>();
        foreach (var hash in file.GetProperty("hashes").EnumerateArray())
            if (hash.GetProperty("algo").GetInt32() == 1) hashes["sha1"] = hash.GetProperty("value").GetString()!;
        var url = file.TryGetProperty("downloadUrl", out var download) && download.ValueKind == JsonValueKind.String ? download.GetString() : null;
        return new ModVersion
        {
            Id = project + "/" + file.GetProperty("id").GetInt32(), ProjectId = project,
            Name = file.GetProperty("displayName").GetString() ?? "", Number = file.GetProperty("fileName").GetString() ?? "",
            GameVersions = games, Loaders = new[] { "forge", "fabric", "neoforge" }.Where(loader => games.Contains(loader, StringComparer.OrdinalIgnoreCase)).ToArray(),
            Dependencies = file.GetProperty("dependencies").EnumerateArray()
                .Where(x => x.GetProperty("relationType").GetInt32() is 3 or 5)
                .Select(x => new ModDependency { ProjectId = "cf:" + x.GetProperty("modId").GetInt32(), Type = x.GetProperty("relationType").GetInt32() == 3 ? "required" : "incompatible" }).ToArray(),
            Files = string.IsNullOrWhiteSpace(url) ? [] : [new(file.GetProperty("fileName").GetString()!, url, hashes, file.GetProperty("fileLength").GetInt64(), true)]
        };
    }
}

public sealed record ModSearchResult(ModProject[] Projects, string Note);
public sealed record ModSourceChoice(ModDownloadSource Mode, string Label) { public override string ToString() => Label; }
public sealed class ModCatalogService(ModrinthService modrinth, CurseForgeService curseforge, LauncherSettingsAccessor settings) : IModCatalog
{
    public async Task<ModSearchResult> Search(string query, GameInstance instance, CancellationToken ct)
    {
        if (instance.Loader == LoaderKind.Vanilla) throw new InvalidOperationException("请先选择 Fabric / Forge / NeoForge 游戏版本，再下载 Mod。");
        var mode = settings.Value.ModDownloadSource;
        if (mode == ModDownloadSource.Modrinth) return new(await modrinth.Search(query, instance, ct), "来源：Modrinth");
        if (mode == ModDownloadSource.CurseForge) return new(await curseforge.Search(query, instance, ct), "来源：CurseForge");
        async Task<(ModProject[] Items, string Note, bool Success)> Try(string name, Func<CancellationToken, Task<ModProject[]>> action)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { return (await action(timeout.Token), name + " 可用", true); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { return ([], name + " 查询超时，已保留其他渠道结果", false); }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            { return ([], name + " 暂不可用，已保留其他渠道结果", false); }
        }
        var mr = Try("Modrinth", token => modrinth.Search(query, instance, token));
        var cf = curseforge.IsConfigured ? Try("CurseForge", token => curseforge.Search(query, instance, token))
            : Task.FromResult((Items: Array.Empty<ModProject>(), Note: "CurseForge 未配置 Key", Success: false));
        var results = await Task.WhenAll(mr, cf);
        if (!results.Any(x => x.Success)) throw new HttpRequestException("Mod 下载渠道均不可用，请检查网络或 CurseForge API Key。");
        // Keep platform identities separate: matching names are not proof of the same project.
        return new(results.SelectMany(x => x.Items).ToArray(), string.Join(" · ", results.Select(x => x.Note)));
    }
    public Task<ModVersion[]> Versions(string project, GameInstance instance, CancellationToken ct) => project.StartsWith("cf:", StringComparison.Ordinal) ? curseforge.Versions(project, instance, ct) : modrinth.Versions(project, instance, ct);
    public Task<ModVersion> Version(string id, CancellationToken ct) => id.StartsWith("cf:", StringComparison.Ordinal) ? curseforge.Version(id, ct) : modrinth.Version(id, ct);
    public Task<JsonElement> Project(string id, CancellationToken ct) => id.StartsWith("cf:", StringComparison.Ordinal) ? curseforge.Project(id, ct) : modrinth.Project(id, ct);
    public static bool SameFile(ModFile a, ModFile b)
    {
        var shared = a.Hashes.Where(x => x.Key is "sha512" or "sha1" && b.Hashes.ContainsKey(x.Key)).ToArray();
        return a.Size == b.Size && shared.Length > 0 && shared.All(x => x.Value.Equals(b.Hashes[x.Key], StringComparison.OrdinalIgnoreCase));
    }
    public async Task Download(ModVersion version, ModFile file, GameInstance instance, string target, DownloadManager downloads, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        var algorithm = file.Hashes.ContainsKey("sha512") ? "sha512" : "sha1";
        var hash = file.Hashes[algorithm];
        try { await downloads.DownloadHash(new Uri(file.Url), target, hash, algorithm, file.Size, progress, ct); return; }
        catch (HttpRequestException) when (settings.Value.ModDownloadSource == ModDownloadSource.Automatic) { }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested && settings.Value.ModDownloadSource == ModDownloadSource.Automatic) { }
        progress.Report(new("原下载渠道不可用，正在寻找校验值相同的官方来源…"));
        var alternates = new List<ModFile>();
        try
        {
            if (version.ProjectId.StartsWith("cf:", StringComparison.Ordinal))
            {
                var match = await modrinth.FromHash(hash, algorithm, ct);
                alternates.AddRange(match.Files.Where(x => SameFile(file, x)));
            }
            else if (curseforge.IsConfigured)
            {
                var project = await modrinth.Project(version.ProjectId, ct);
                var slug = project.GetProperty("slug").GetString()!;
                foreach (var candidate in (await curseforge.Search(slug, instance, ct)).Take(3))
                {
                    await curseforge.Project(candidate.Id, ct);
                    foreach (var match in await curseforge.Versions(candidate.Id, instance, ct)) alternates.AddRange(match.Files.Where(x => SameFile(file, x)));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
        foreach (var alternate in alternates.DistinctBy(x => x.Url))
        {
            if (alternate.Url == file.Url) continue;
            try { await downloads.DownloadHash(new Uri(alternate.Url), target, hash, algorithm, file.Size, progress, ct); return; }
            catch (HttpRequestException) { }
        }
        throw new IOException("下载失败，未找到校验值相同的可用来源：" + file.Filename + "。请稍后重试或切换渠道。");
    }
}
