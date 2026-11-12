using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineOrbit.Core;

namespace MineOrbit.Infrastructure;

public sealed record PackFolderManifest(int FormatVersion, string ProfileId, string Name, string MinecraftVersion,
    LoaderKind Loader, string LoaderVersion, string LaunchVersion, string? JavaExecutable,
    int MinimumRamMb, int MaximumRamMb, int Width, int Height, DateTimeOffset Created);
public sealed record PackFolderResult(string Directory, PackFolderManifest Manifest);

public sealed class PackFolderExportService(PackInstallationService importer, LauncherSettingsAccessor settings)
{
    public const string ManifestName = "mineorbit-export.json";
    public async Task<PackFolderResult> Export(string archive, PackImportOptions options, string destination, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        var target = ValidateDestination(destination);
        foreach (var managed in new[] { settings.Value.DataRoot, CachePaths.Resolve(settings.Value, settings.Value.LibrariesPath, "libraries"), CachePaths.Resolve(settings.Value, settings.Value.AssetsPath, "assets"), CachePaths.Resolve(settings.Value, settings.Value.RuntimePath, "runtime"), Path.Combine(settings.Value.DataRoot, "instances") })
        {
            var canonical = Path.GetFullPath(managed).TrimEnd(Path.DirectorySeparatorChar);
            if (target.Equals(canonical, StringComparison.OrdinalIgnoreCase) || canonical != Path.GetFullPath(settings.Value.DataRoot).TrimEnd(Path.DirectorySeparatorChar) && target.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择独立的导出文件夹，不能使用游戏数据根目录、运行库、资源缓存或游戏版本目录。");
        }
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent); GameDirectoryService.EnsureNoLinks(parent);
        var work = PathSafety.Child(parent, ".mineorbit-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var manager = new InstanceManager(Path.Combine(work, "install"));
            progress.Report(new("正在安装整合包并准备独立文件夹…"));
            var instance = await importer.Import(archive, options, manager, progress, ct);
            var prepared = Path.Combine(work, "game");
            var paths = CachePaths.Minecraft(settings.Value, instance.Directory);
            var manifest = await PrepareFolder(instance, paths.Library, paths.Assets, paths.Runtime, prepared, target, settings.Value, progress, ct);
            ct.ThrowIfCancellationRequested();
            ValidateDestination(target);
            if (Directory.Exists(target)) Directory.Delete(target, false); // Only a checked, empty user-selected directory.
            Directory.Move(prepared, target);
            progress.Report(new("已导出完整游戏文件夹，可添加到其他启动器。", 1));
            return new(target, manifest);
        }
        finally
        {
            // The unique work directory is always a child of the selected destination's parent.
            if (Directory.Exists(work)) { GameDirectoryService.EnsureNoLinks(work); Directory.Delete(work, true); }
        }
    }
    public static string ValidateDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination)) throw new ArgumentException("请选择导出文件夹的完整路径。");
        var target = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(target) is not { Length: > 0 }) throw new ArgumentException("不能导出到磁盘根目录。");
        GameDirectoryService.EnsureNoLinks(target);
        if (File.Exists(target) || Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("导出文件夹必须不存在或为空；请选择一个新文件夹，以免混合游戏版本。");
        return target;
    }
    public static async Task<PackFolderManifest> ReadManifest(string root, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root); GameDirectoryService.EnsureNoLinks(root);
        var file = Path.Combine(root, ManifestName);
        ExportFiles.RequireRegularFile(file);
        if (new FileInfo(file).Length > 1024 * 1024) throw new InvalidDataException("导出信息文件过大。");
        var manifest = await JsonStore.Read<PackFolderManifest>(file, ct) ?? throw new InvalidDataException("未找到 MineOrbit 导出的游戏文件夹。");
        if (manifest.FormatVersion != 1 || !System.Text.RegularExpressions.Regex.IsMatch(manifest.ProfileId ?? "", "^mineorbit-[0-9a-f]{32}$")) throw new InvalidDataException("不支持的导出信息格式。");
        ExportFiles.VersionFile(root, manifest.LaunchVersion);
        new LauncherSettings { DataRoot = root, MinimumRamMb = manifest.MinimumRamMb, MaximumRamMb = manifest.MaximumRamMb, Width = manifest.Width, Height = manifest.Height }.Validate();
        if (manifest.JavaExecutable != null)
        {
            if (!manifest.JavaExecutable.StartsWith("runtime/", StringComparison.Ordinal) || Path.GetFileName(manifest.JavaExecutable) is not ("java.exe" or "javaw.exe")) throw new InvalidDataException("导出 Java 路径无效。");
            ExportFiles.RequireRegularFile(PathSafety.Child(root, manifest.JavaExecutable));
        }
        return manifest;
    }
    public static async Task<PackFolderManifest> PrepareFolder(GameInstance instance, string libraries, string assets, string runtime,
        string output, string finalDirectory, LauncherSettings options, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("临时导出目录必须为空。");
        Directory.CreateDirectory(output); GameDirectoryService.EnsureNoLinks(output);
        var chain = await ExportFiles.VersionChain(instance.Directory, instance.LaunchVersion, ct);
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "versions", "libraries", "assets", "runtime", ".mineorbit", "instance.json", ".pending.json", ManifestName, "accounts.json", "settings.json" };
        foreach (var entry in Directory.EnumerateFileSystemEntries(instance.Directory))
        {
            var name = Path.GetFileName(entry);
            if (skipped.Contains(name) || name.StartsWith("launcher_", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".secrets", StringComparison.OrdinalIgnoreCase)) continue;
            await ExportFiles.CopyTree(entry, PathSafety.Child(output, name), ct);
        }
        foreach (var (id, json) in chain)
        {
            await ExportFiles.CopyFile(ExportFiles.VersionFile(instance.Directory, id), PathSafety.Child(output, $"versions/{id}/{id}.json"), ct);
            var jar = PathSafety.Child(instance.Directory, $"versions/{id}/{id}.jar");
            if (File.Exists(jar)) await ExportFiles.CopyFile(jar, PathSafety.Child(output, $"versions/{id}/{id}.jar"), ct, json["downloads"]?["client"]?["sha1"]?.GetValue<string>());
            else if (json["inheritsFrom"] == null && json["jar"] == null && json["downloads"]?["client"] != null) throw new FileNotFoundException("缺少 Minecraft 游戏本体 JAR，请先修复安装。");
            foreach (var relative in ExportFiles.LibraryPaths(json))
            {
                var source = PathSafety.Child(libraries, relative.Path);
                if (File.Exists(source)) await ExportFiles.CopyFile(source, PathSafety.Child(output, "libraries/" + relative.Path), ct, relative.Sha1);
                else if (relative.Required) throw new FileNotFoundException("缺少已安装的运行库，请先修复游戏：" + relative.Path);
            }
        }
        progress.Report(new("正在复制所需 Assets 和运行库…"));
        var indexIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, json) in chain)
        {
            var assetIndex = json["assetIndex"] as JsonObject;
            var id = assetIndex?["id"]?.GetValue<string>() ?? json["assets"]?.GetValue<string>();
            if (id != null && indexIds.Add(id))
            {
                var source = PathSafety.Child(assets, "indexes/" + id + ".json");
                await ExportFiles.CopyFile(source, PathSafety.Child(output, "assets/indexes/" + id + ".json"), ct, assetIndex?["sha1"]?.GetValue<string>());
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(source, ct));
                var index = document.RootElement;
                var virtualAssets = index.TryGetProperty("virtual", out var v) && v.GetBoolean();
                var resources = index.TryGetProperty("map_to_resources", out var m) && m.GetBoolean();
                foreach (var item in index.GetProperty("objects").EnumerateObject())
                {
                    ct.ThrowIfCancellationRequested();
                    var hash = item.Value.GetProperty("hash").GetString()!;
                    if (hash.Length != 40 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("资源索引的 Hash 无效。");
                    var relative = "objects/" + hash[..2] + "/" + hash;
                    var original = PathSafety.Child(assets, relative);
                    await ExportFiles.CopyFile(original, PathSafety.Child(output, "assets/" + relative), ct, hash);
                    if (virtualAssets) await ExportFiles.CopyFile(original, PathSafety.Child(output, "assets/virtual/" + id + "/" + item.Name), ct, hash);
                    var resourceTarget = resources ? PathSafety.Child(output, "resources/" + item.Name) : null;
                    if (resourceTarget != null && !File.Exists(resourceTarget)) await ExportFiles.CopyFile(original, resourceTarget, ct, hash);
                }
            }
            if (json["logging"]?["client"]?["file"] is JsonObject logging && logging["id"]?.GetValue<string>() is { } logId)
                await ExportFiles.CopyFile(PathSafety.Child(assets, "log_configs/" + logId), PathSafety.Child(output, "assets/log_configs/" + logId), ct, logging["sha1"]?.GetValue<string>());
        }
        string? java = null;
        if (Directory.Exists(runtime))
        {
            // Only bundled runtimes are eligible; never copy a user-configured external JDK directory.
            var javaFiles = ExportFiles.FilesUnder(runtime).Where(x => Path.GetFileName(x).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
            var component = chain.Select(x => x.Json["javaVersion"]?["component"]?.GetValue<string>()).FirstOrDefault(x => x != null);
            var candidates = javaFiles.Where(x => component == null || Path.GetRelativePath(runtime, x).Split(Path.DirectorySeparatorChar).Contains(component, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 1)
            {
                var bin = Path.GetDirectoryName(candidates[0])!;
                var home = Path.GetDirectoryName(bin)!;
                var relative = Path.GetRelativePath(Path.GetFullPath(runtime), home);
                if (Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                {
                    GameDirectoryService.EnsureNoLinks(home);
                    await ExportFiles.CopyTree(home, Path.Combine(output, "runtime", "java"), ct);
                    java = "runtime/java/bin/javaw.exe";
                }
            }
        }
        var manifest = new PackFolderManifest(1, "mineorbit-" + Guid.NewGuid().ToString("N"), instance.Name, instance.MinecraftVersion, instance.Loader,
            instance.LoaderVersion, instance.LaunchVersion, java, options.MinimumRamMb, options.MaximumRamMb, options.Width, options.Height, DateTimeOffset.UtcNow);
        await JsonStore.Write(Path.Combine(output, ManifestName), manifest, ct);
        await JsonStore.Write(Path.Combine(output, "launcher_profiles.json"), new JsonObject { ["profiles"] = new JsonObject { [manifest.ProfileId] = ExportFiles.LauncherProfile(manifest, finalDirectory, manifest.LaunchVersion) }, ["version"] = 3 }, ct);
        await File.WriteAllTextAsync(Path.Combine(output, "MineOrbit-启动说明.txt"),
            $"{instance.Name}\nMinecraft {instance.MinecraftVersion} / {instance.Loader} {instance.LoaderVersion}\n\n" +
            "PCL / HMCL 等支持标准 .minecraft 目录的启动器：添加此文件夹为游戏目录，选择版本 " + instance.LaunchVersion + "；将该版本的实际游戏运行目录也设为此文件夹（使用根目录 / 关闭版本隔离），再使用正版账号启动。请确认它使用的是此处的 mods / config / saves。\n\n" +
            "官方 Minecraft 启动器：仅修改游戏目录不会导入这个版本。请先关闭官方启动器，在 MineOrbit 的整合包页选择此导出文件夹，点击“添加到官方启动器”；随后打开官方启动器，在 Java 版的安装配置中选择本整合包。看不到时勾选“修改版 / Modded”。\n\n" +
            "此文件夹包含游戏版本、所需资源、运行库、Mods 和配置，不包含任何账号或 Token。游戏登录由所选启动器完成。移动文件夹后需要重新添加启动项。\n" +
            (java == null ? "未内置 Java；请由目标启动器自动下载或选择此游戏所需的 x64 Java。\n" : "已包含所需的 bundled Java，添加官方启动项时会使用此 Java。\n"), ct);
        return manifest;
    }
}

public static class ExportFiles
{
    public static void RequireRegularFile(string path)
    {
        GameDirectoryService.EnsureNoLinks(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) throw new FileNotFoundException("缺少必要文件：" + Path.GetFileName(path));
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("不能复制文件链接。");
    }
    public static string VersionFile(string root, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(['/', '\\', ':']) >= 0) throw new InvalidDataException("版本标识无效。");
        var file = PathSafety.Child(root, $"versions/{id}/{id}.json"); RequireRegularFile(file); return file;
    }
    public static async Task<List<(string Id, JsonObject Json)>> VersionChain(string root, string id, CancellationToken ct)
    {
        var result = new List<(string, JsonObject)>(); var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task Visit(string current)
        {
            if (visiting.Contains(current)) throw new InvalidDataException("版本继承存在循环。");
            if (result.Any(x => x.Item1.Equals(current, StringComparison.OrdinalIgnoreCase))) return;
            if (result.Count >= 32) throw new InvalidDataException("版本继承层数过多。");
            visiting.Add(current);
            var file = VersionFile(root, current);
            if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("版本配置过大。");
            var json = JsonNode.Parse(await File.ReadAllTextAsync(file, ct)) as JsonObject ?? throw new InvalidDataException("版本配置无效。");
            if (json["id"]?.GetValue<string>() != current) throw new InvalidDataException("版本文件中的 ID 不一致。");
            result.Add((current, json));
            if (json["inheritsFrom"]?.GetValue<string>() is { } parent) await Visit(parent);
            if (json["jar"]?.GetValue<string>() is { } jar && jar != current) await Visit(jar);
            visiting.Remove(current);
        }
        await Visit(id); return result;
    }
    public static async Task CopyFile(string source, string target, CancellationToken ct, string? sha1 = null)
    {
        RequireRegularFile(source); GameDirectoryService.EnsureNoLinks(Path.GetDirectoryName(target)!);
        if (!string.IsNullOrWhiteSpace(sha1)) await DownloadManager.VerifyHash(source, sha1, "sha1", ct);
        if (File.Exists(target))
        {
            RequireRegularFile(target);
            if (!await SameFile(source, target, ct)) throw new IOException("目标目录存在不同内容的文件，已保留原文件：" + Path.GetFileName(target));
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, ct);
    }
    public static async Task<bool> SameFile(string a, string b, CancellationToken ct)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        await using var left = File.OpenRead(a); await using var right = File.OpenRead(b);
        var leftHash = await SHA256.HashDataAsync(left, ct); var rightHash = await SHA256.HashDataAsync(right, ct);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }
    public static IEnumerable<string> FilesUnder(string root)
    {
        GameDirectoryService.EnsureNoLinks(root);
        foreach (var file in Directory.EnumerateFiles(root)) { RequireRegularFile(file); yield return file; }
        foreach (var directory in Directory.EnumerateDirectories(root)) foreach (var file in FilesUnder(directory)) yield return file;
    }
    public static async Task CopyTree(string source, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (File.Exists(source)) { await CopyFile(source, target, ct); return; }
        GameDirectoryService.EnsureNoLinks(source); GameDirectoryService.EnsureNoLinks(target);
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source)) await CopyTree(entry, PathSafety.Child(target, Path.GetFileName(entry)), ct);
    }
    public static IEnumerable<(string Path, string? Sha1, bool Required)> LibraryPaths(JsonObject version)
    {
        foreach (var lib in (version["libraries"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!AppliesToWindows(lib)) continue;
            var downloads = lib["downloads"] as JsonObject;
            if (downloads?["artifact"] is JsonObject artifact && artifact["path"]?.GetValue<string>() is { } path)
                yield return (path, artifact["sha1"]?.GetValue<string>(), true);
            else if (lib["name"]?.GetValue<string>() is { } name) yield return (MavenPath(name), null, lib["natives"] == null);
            if (downloads?["classifiers"] is JsonObject classifiers)
                foreach (var (_, node) in classifiers)
                    if (node is JsonObject item && item["path"]?.GetValue<string>() is { } nativePath) yield return (nativePath, item["sha1"]?.GetValue<string>(), false);
            if (lib["natives"]?["windows"]?.GetValue<string>() is { } native && lib["name"]?.GetValue<string>() is { } nativeName)
                yield return (MavenPath(nativeName, native.Replace("${arch}", "64", StringComparison.Ordinal)), null, true);
        }
    }
    public static string MavenPath(string name, string? classifier = null)
    {
        var extension = "jar"; var at = name.IndexOf('@');
        if (at >= 0) { extension = name[(at + 1)..]; name = name[..at]; }
        var parts = name.Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("运行库坐标无效。");
        classifier ??= parts.ElementAtOrDefault(3);
        var path = $"{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}{(classifier == null ? "" : "-" + classifier)}.{extension}";
        PathSafety.Child(Path.GetTempPath(), path); return path;
    }
    private static bool AppliesToWindows(JsonObject library)
    {
        if (library["rules"] is not JsonArray rules || rules.Count == 0) return true;
        var allow = false;
        foreach (var rule in rules.OfType<JsonObject>())
        {
            var os = rule["os"] as JsonObject;
            if (os?["name"]?.GetValue<string>() is { } name && name != "windows") continue;
            if (os?["arch"]?.GetValue<string>() is { } arch && !new[] { "x86_64", "amd64", "x64" }.Any(x => System.Text.RegularExpressions.Regex.IsMatch(x, "\\A(?:" + arch + ")\\z"))) continue;
            if (os?["version"]?.GetValue<string>() is { } v && !System.Text.RegularExpressions.Regex.IsMatch(Environment.OSVersion.Version.ToString(), v)) continue;
            if (rule["features"] is JsonObject features && features.Any(x => x.Value?.GetValue<bool>() == true)) continue;
            allow = rule["action"]?.GetValue<string>() == "allow";
        }
        return allow;
    }
    public static JsonObject LauncherProfile(PackFolderManifest manifest, string gameDirectory, string version)
    {
        var profile = new JsonObject { ["name"] = manifest.Name, ["type"] = "custom", ["lastVersionId"] = version,
            ["gameDir"] = Path.GetFullPath(gameDirectory), ["created"] = manifest.Created.ToString("O"), ["lastUsed"] = manifest.Created.ToString("O"),
            ["icon"] = "Grass", ["javaArgs"] = $"-Xms{manifest.MinimumRamMb}M -Xmx{manifest.MaximumRamMb}M",
            ["resolution"] = new JsonObject { ["width"] = manifest.Width, ["height"] = manifest.Height } };
        if (manifest.JavaExecutable != null) profile["javaDir"] = PathSafety.Child(gameDirectory, manifest.JavaExecutable);
        return profile;
    }
}
