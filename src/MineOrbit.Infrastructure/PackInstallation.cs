using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using MineOrbit.Core;

namespace MineOrbit.Infrastructure;
public record PackImportOptions(string Name, string? Minecraft, LoaderKind Loader, string? LoaderVersion, bool IncludeOptional = false);
public sealed class PackInstallationService(ModpackImportService inspector, MinecraftInstallationService installer, DownloadManager downloads, HttpClient http, LauncherSettingsAccessor settings)
{
    private sealed record PackFile(string Relative, string[] Urls, string Hash, string Algorithm, long Size);
    public static string PayloadPath(string root, string relative)
    {
        var target = PathSafety.Child(root, relative);
        var normalized = relative.Replace('\\', '/');
        var first = normalized.Split('/')[0];
        if (new[] { "instance.json", ".pending.json", ".mineorbit", "versions", "libraries", "assets", "runtime", ".git" }.Contains(first, StringComparer.OrdinalIgnoreCase)
            || new[] { ".exe", ".dll", ".bat", ".cmd", ".ps1", ".lnk" }.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("整合包不能覆盖启动器元数据、运行时或安装系统可执行文件。");
        return target;
    }
    public static (string Minecraft, LoaderKind Loader, string LoaderVersion) ReadDependencies(JsonElement dependencies)
    {
        var mc = dependencies.GetProperty("minecraft").GetString() ?? throw new InvalidDataException("未声明 Minecraft。");
        LoaderKind loader = LoaderKind.Vanilla; string loaderVersion = "";
        foreach (var property in dependencies.EnumerateObject().Where(x => x.Name != "minecraft"))
        {
            if (loader != LoaderKind.Vanilla) throw new InvalidDataException("整合包声明了多个 Loader。");
            loader = property.Name switch { "fabric-loader" => LoaderKind.Fabric, "forge" => LoaderKind.Forge, "neoforge" => LoaderKind.NeoForge, _ => throw new NotSupportedException("尚不支持此整合包依赖：" + property.Name) };
            loaderVersion = property.Value.GetString() ?? throw new InvalidDataException("缺失 Loader 版本。");
        }
        return (mc, loader, loaderVersion);
    }
    public async Task<GameInstance> Import(string archive, PackImportOptions options, InstanceManager manager, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        // Keep the archive read-locked from inspection through extraction.
        await using var archiveStream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        var inspection = await inspector.Inspect(archive, ct);
        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        var mr = inspection.Format == PackFormat.Modrinth ? zip.Entries.Single(x => ModpackImportService.Normalize(x.FullName) == inspection.ManifestPath) : null;
        var cf = inspection.Format == PackFormat.CurseForge ? zip.Entries.Single(x => ModpackImportService.Normalize(x.FullName) == inspection.ManifestPath) : null;
        var payload = ModpackImportService.Payload(zip, inspection);
        var gameVersion = options.Minecraft; var loader = options.Loader; var loaderVersion = options.LoaderVersion ?? "";
        if (inspection.Format == PackFormat.MultiMC) { gameVersion = inspection.Minecraft; loader = inspection.DetectedLoader!.Value; loaderVersion = inspection.LoaderVersion; }
        var files = new List<PackFile>(); var overrideFolder = "overrides";
        if (mr != null)
        {
            await using var stream = mr.Open(); using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement; (gameVersion, loader, loaderVersion) = ReadDependencies(root.GetProperty("dependencies"));
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in root.GetProperty("files").EnumerateArray())
            {
                var relative = file.GetProperty("path").GetString() ?? throw new InvalidDataException("清单路径为空。");
                PayloadPath(manager.Root, relative);
                if (!paths.Add(relative.Replace('\\', '/'))) throw new InvalidDataException("MRPACK 清单路径重复。");
                if (file.TryGetProperty("env", out var env) && env.TryGetProperty("client", out var client))
                {
                    var mode = client.GetString();
                    if (mode == "unsupported" || mode == "optional" && !options.IncludeOptional) continue;
                    if (mode is not ("required" or "optional" or "unsupported")) throw new InvalidDataException("未知客户端环境规则。");
                }
                var urls = file.GetProperty("downloads").EnumerateArray().Select(x => x.GetString()!).ToArray();
                if (urls.Length == 0 || urls.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !new[] { "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com" }.Contains(uri.Host)))
                    throw new InvalidDataException("MRPACK 下载地址不在允许的 HTTPS 来源中。");
                var hashes = file.GetProperty("hashes");
                if (!hashes.TryGetProperty("sha1", out _) || !hashes.TryGetProperty("sha512", out var sha512)) throw new InvalidDataException("MRPACK 必须包含 SHA1 和 SHA512。");
                files.Add(new(relative, urls, sha512.GetString()!, "sha512", file.GetProperty("fileSize").GetInt64()));
            }
        }
        else if (cf != null)
        {
            await using var stream = cf.Open(); using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement; var mc = root.GetProperty("minecraft"); gameVersion = mc.GetProperty("version").GetString();
            var primary = mc.GetProperty("modLoaders").EnumerateArray().Where(x => x.TryGetProperty("primary", out var p) && p.GetBoolean()).ToArray();
            if (primary.Length == 0) primary = mc.GetProperty("modLoaders").EnumerateArray().ToArray();
            if (primary.Length > 1) throw new InvalidDataException("无法确定 CurseForge 主 Loader。");
            if (primary.Length == 1)
            {
                var id = primary[0].GetProperty("id").GetString()!; var split = id.IndexOf('-');
                if (split < 1) throw new InvalidDataException("Loader 标识无效。");
                loader = id[..split] switch { "forge" => LoaderKind.Forge, "fabric" => LoaderKind.Fabric, "neoforge" => LoaderKind.NeoForge, _ => throw new NotSupportedException("不支持的 CurseForge Loader。") }; loaderVersion = id[(split + 1)..];
            }
            if (root.TryGetProperty("overrides", out var ov)) overrideFolder = ov.GetString()!;
            PathSafety.Child(manager.Root, overrideFolder);
            var missing = new List<string>();
            foreach (var item in root.GetProperty("files").EnumerateArray())
            {
                var project = item.GetProperty("projectID").GetInt32(); var id = item.GetProperty("fileID").GetInt32();
                if (item.TryGetProperty("required", out var required) && !required.GetBoolean() && !options.IncludeOptional) continue;
                var apiKey = settings.Value.CurseForgeApiKey;
                if (string.IsNullOrWhiteSpace(apiKey)) { missing.Add($"项目 {project} / 文件 {id}：需要授权的 CurseForge API Key"); continue; }
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.curseforge.com/v1/mods/{project}/files/{id}"); request.Headers.Add("x-api-key", apiKey);
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) { missing.Add($"项目 {project} / 文件 {id}：API HTTP {(int)response.StatusCode}"); continue; }
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct); var data = doc.RootElement.GetProperty("data");
                if (data.GetProperty("id").GetInt32() != id || data.GetProperty("modId").GetInt32() != project) throw new InvalidDataException("CurseForge 返回了与整合包清单不一致的项目或文件。");
                var url = data.GetProperty("downloadUrl").GetString();
                if (string.IsNullOrEmpty(url)) { missing.Add($"项目 {project} / 文件 {id}：作者禁止第三方自动下载"); continue; }
                var sha1 = data.GetProperty("hashes").EnumerateArray().FirstOrDefault(x => x.GetProperty("algo").GetInt32() == 1);
                if (sha1.ValueKind == JsonValueKind.Undefined) { missing.Add($"项目 {project} / 文件 {id}：缺少 SHA1"); continue; }
                var filename = data.GetProperty("fileName").GetString()!;
                if (Path.GetFileName(filename) != filename) throw new InvalidDataException("CurseForge 文件名不安全。");
                files.Add(new("mods/" + filename, [url], sha1.GetProperty("value").GetString()!, "sha1", data.GetProperty("fileLength").GetInt64()));
            }
            if (missing.Count > 0) throw new InvalidOperationException("无法自动安装以下 CurseForge 文件，不会绕过授权或发布缺失游戏版本：\n" + string.Join("\n", missing));
        }
        else if (string.IsNullOrWhiteSpace(gameVersion) || loader != LoaderKind.Vanilla && string.IsNullOrWhiteSpace(loaderVersion))
            throw new ArgumentException("普通 ZIP 必须手动填写 Minecraft、Loader 和 Loader 版本。");
        if (inspection.Format == PackFormat.PlainZip && loader == LoaderKind.Vanilla && payload.Any(x => x.Relative.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && x.Relative.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("ZIP 中包含 Mod 文件，请选择正确的 Fabric / Forge / NeoForge 及具体版本，不能按原版导入。");
        if (files.Count > 50000 || files.Any(x => x.Size is <= 0 or > 2147483648) || files.Sum(x => x.Size) > 20L * 1024 * 1024 * 1024) throw new InvalidDataException("整合包下载容量超出安全限制。");
        if (files.GroupBy(x => x.Relative.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) throw new InvalidDataException("整合包的下载文件存在重复目标路径。");
        var instance = manager.New(options.Name, gameVersion!, loader, loaderVersion);
        if (Directory.Exists(instance.Directory)) throw new IOException("新游戏版本目录已存在。");
        Directory.CreateDirectory(instance.Directory);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(instance.Directory)!);
            if (drive.IsReady && drive.AvailableFreeSpace < inspection.UncompressedBytes + files.Sum(x => x.Size) + 100L * 1024 * 1024) throw new IOException("目标磁盘空间不足，无法解压和下载此整合包。");
            // All content is written to a fresh private instance. Publish its metadata only after complete validation.
            await installer.Install(instance, manager, progress, ct, publish: false);
            var batch = new ParallelOptions { MaxDegreeOfParallelism = settings.Value.DownloadConcurrency, CancellationToken = ct };
            await Parallel.ForEachAsync(files, batch, async (file, token) =>
            {
                var target = PayloadPath(instance.Directory, file.Relative); Exception? last = null;
                foreach (var url in file.Urls)
                {
                    try { await downloads.DownloadHash(new Uri(url), target, file.Hash, file.Algorithm, file.Size, progress, token); if (new FileInfo(target).Length != file.Size) throw new InvalidDataException("清单文件大小不一致。"); return; }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException) { last = ex; }
                }
                throw new IOException("整合包文件下载失败：" + file.Relative, last);
            });
            await Extract(payload, instance.Directory, progress, ct);
            if (inspection.ManifestPath is { } manifestPath)
            {
                var manifest = zip.Entries.Single(x => ModpackImportService.Normalize(x.FullName) == manifestPath);
                var metadata = Path.Combine(instance.Directory, ".mineorbit"); Directory.CreateDirectory(metadata);
                await using var source = manifest.Open(); await using var output = File.Create(Path.Combine(metadata, "original-manifest.json")); await source.CopyToAsync(output, ct);
            }
            await JsonStore.Write(Path.Combine(instance.Directory, ".mineorbit", "pack.json"), new { Source = Path.GetFileName(archive), options.IncludeOptional, Imported = DateTimeOffset.Now }, ct);
            await manager.Save(instance, ct); progress.Report(new("整合包安装完成", 1)); return instance;
        }
        catch
        {
            manager.Validate(instance);
            GameDirectoryService.EnsureNoLinks(instance.Directory);
            Directory.Delete(instance.Directory, true); throw;
        }
    }
    public static async Task Extract(IReadOnlyList<(ZipArchiveEntry Entry, string Relative)> payload, string targetRoot, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        long total = 0; var completed = 0;
        foreach (var (entry, relative) in payload)
        {
            ct.ThrowIfCancellationRequested();
            var target = PayloadPath(targetRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            GameDirectoryService.EnsureNoLinks(Path.GetDirectoryName(target)!);
            await using var input = entry.Open(); await using var output = File.Create(target);
            var buffer = new byte[81920]; int read; long fileSize = 0;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                fileSize += read; total += read;
                if (fileSize > entry.Length || total > 20L * 1024 * 1024 * 1024) throw new InvalidDataException("实际解压大小超出清单或安全限制。");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (fileSize != entry.Length) throw new InvalidDataException("压缩包文件长度不一致。");
            progress.Report(new($"解压整合包 {++completed}/{payload.Count} · {relative}", (double)completed / payload.Count));
        }
    }
}
