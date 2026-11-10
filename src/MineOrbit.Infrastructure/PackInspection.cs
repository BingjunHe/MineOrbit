using System.IO.Compression;
using System.Text.Json;
using MineOrbit.Core;

namespace MineOrbit.Infrastructure;

public enum PackFormat { PlainZip, Modrinth, CurseForge, MultiMC }
public record PackInfo(string Kind, string? Minecraft, string? Loader, int FileCount)
{
    public PackFormat Format { get; init; }
    public string RootPrefix { get; init; } = "";
    public string PayloadPrefix { get; init; } = "";
    public string? ManifestPath { get; init; }
    public string Name { get; init; } = "";
    public LoaderKind? DetectedLoader { get; init; }
    public string LoaderVersion { get; init; } = "";
    public string Notes { get; init; } = "";
    public long UncompressedBytes { get; init; }
}
public sealed class ModpackImportService
{
    // Inspection remains side-effect free; PackInstallationService handles staged installation.
    public async Task<PackInfo> Inspect(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count > 50000) throw new InvalidDataException("文件数超过安全限制。");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var relative = e.FullName.TrimEnd('/', '\\');
            if (relative.Length == 0) continue;
            PathSafety.Child(Path.GetTempPath(), relative);
            if (!names.Add(relative.Replace('\\', '/'))) throw new InvalidDataException("压缩包存在重复路径。");
            if (((e.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("压缩包包含符号链接。");
            total = checked(total + e.Length);
            if (total > 20L * 1024 * 1024 * 1024 || e.Length > 2L * 1024 * 1024 * 1024 || (e.Length > 1024 * 1024 && e.Length / Math.Max(e.CompressedLength, 1) > 1000))
                throw new InvalidDataException("压缩包超出解压安全限制。");
        }
        if (!zip.Entries.Any(x => !x.FullName.EndsWith('/'))) throw new InvalidDataException("整合包为空。");
        var manifests = new List<(ZipArchiveEntry Entry, PackFormat Format)>();
        foreach (var entry in zip.Entries.Where(x => Normalize(x.FullName).Split('/').Length <= 5 && x.Name is "modrinth.index.json" or "manifest.json" or "mmc-pack.json"))
        {
            using var document = await ReadJson(entry, ct);
            var element = document.RootElement;
            if (entry.Name == "modrinth.index.json") manifests.Add((entry, PackFormat.Modrinth));
            else if (entry.Name == "mmc-pack.json") manifests.Add((entry, PackFormat.MultiMC));
            else if (element.TryGetProperty("minecraft", out _)) manifests.Add((entry, PackFormat.CurseForge));
        }
        if (manifests.Count > 1) throw new InvalidDataException("压缩包中发现多个整合包清单，请分别导入，不能猜测要使用哪一个。");
        var info = new PackInfo("完整文件 ZIP", null, null, zip.Entries.Count) { Name = Path.GetFileNameWithoutExtension(path), UncompressedBytes = total };
        if (manifests.Count == 1)
        {
            var (manifest, format) = manifests[0];
            var manifestPath = Normalize(manifest.FullName);
            var prefix = manifestPath[..(manifestPath.Length - manifest.Name.Length)];
            using var document = await ReadJson(manifest, ct);
            var root = document.RootElement;
            string minecraft; LoaderKind loader = LoaderKind.Vanilla; string loaderVersion = "";
            if (format == PackFormat.Modrinth)
            {
                if (root.GetProperty("formatVersion").GetInt32() != 1 || root.GetProperty("game").GetString() != "minecraft") throw new InvalidDataException("不支持的 MRPACK 版本或游戏。");
                (minecraft, loader, loaderVersion) = PackInstallationService.ReadDependencies(root.GetProperty("dependencies"));
            }
            else if (format == PackFormat.CurseForge)
            {
                if (root.TryGetProperty("manifestType", out var type) && type.GetString() != "minecraftModpack" || root.TryGetProperty("manifestVersion", out var v) && v.GetInt32() != 1) throw new InvalidDataException("不支持的 CurseForge 清单格式。");
                var mc = root.GetProperty("minecraft"); minecraft = mc.GetProperty("version").GetString()!;
                var loaders = mc.GetProperty("modLoaders").EnumerateArray().ToArray();
                var primary = loaders.Where(x => x.TryGetProperty("primary", out var p) && p.GetBoolean()).ToArray();
                if (primary.Length == 0) primary = loaders;
                if (primary.Length > 1) throw new InvalidDataException("无法确定 CurseForge 主 Loader。");
                if (primary.Length == 1) (loader, loaderVersion) = ParseLoader(primary[0].GetProperty("id").GetString()!);
            }
            else
            {
                if (zip.Entries.Any(x => !x.FullName.EndsWith('/') && (Normalize(x.FullName).StartsWith(prefix + "patches/", StringComparison.Ordinal) || Normalize(x.FullName).StartsWith(prefix + "jarmods/", StringComparison.Ordinal))))
                    throw new NotSupportedException("此 MultiMC 包包含自定义版本补丁或 Jar Mods，当前不能可靠转换，请先导出标准整合包。");
                if (root.GetProperty("formatVersion").GetInt32() != 1) throw new InvalidDataException("不支持的 MultiMC 格式。");
                var components = root.GetProperty("components").EnumerateArray().Where(x => !x.TryGetProperty("disabled", out var disabled) || !disabled.GetBoolean()).ToArray();
                minecraft = components.Single(x => x.GetProperty("uid").GetString() == "net.minecraft").GetProperty("version").GetString()!;
                foreach (var component in components.Where(x => x.GetProperty("uid").GetString() != "net.minecraft"))
                {
                    var uid = component.GetProperty("uid").GetString()!;
                    if (uid is "org.lwjgl" or "org.lwjgl3") continue;
                    if (loader != LoaderKind.Vanilla) throw new InvalidDataException("MultiMC 清单声明多个 Loader。");
                    loader = uid switch { "net.fabricmc.fabric-loader" => LoaderKind.Fabric, "net.minecraftforge" => LoaderKind.Forge, "net.neoforged" => LoaderKind.NeoForge, _ => throw new NotSupportedException("不支持的 MultiMC 组件：" + uid) };
                    loaderVersion = component.GetProperty("version").GetString()!;
                }
            }
            var payload = format == PackFormat.MultiMC ? GameFolder(zip, prefix) : prefix;
            info = info with { Format = format, RootPrefix = prefix, PayloadPrefix = payload, ManifestPath = manifestPath,
                Minecraft = minecraft, DetectedLoader = loader, LoaderVersion = loaderVersion, Loader = loader.ToString().ToLowerInvariant() + " " + loaderVersion,
                Kind = format switch { PackFormat.Modrinth => "Modrinth MRPACK", PackFormat.CurseForge => "CurseForge 整合包", _ => "MultiMC / Prism 整合包" },
                Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? info.Name : info.Name };
        }
        else
        {
            var prefix = CommonWrapper(zip);
            var gameRoot = GameFolder(zip, prefix);
            var payload = IsolatedGameFolder(zip, gameRoot);
            var profiles = new List<(string Minecraft, LoaderKind Loader, string Version)>();
            foreach (var entry in zip.Entries.Where(x => Normalize(x.FullName).StartsWith(gameRoot + "versions/", StringComparison.Ordinal) && x.Name.EndsWith(".json", StringComparison.Ordinal)))
            {
                using var json = await ReadJson(entry, ct); var profile = json.RootElement;
                var mc = profile.TryGetProperty("inheritsFrom", out var parent) ? parent.GetString() : profile.TryGetProperty("id", out var id) ? id.GetString() : null;
                if (mc == null || !System.Text.RegularExpressions.Regex.IsMatch(mc, @"^(\d+\.\d+(\.\d+)?|\d{2}w\d{2}[a-z])$")) continue;
                LoaderKind loader = LoaderKind.Vanilla; var loaderVersion = "";
                if (profile.TryGetProperty("libraries", out var libraries))
                    foreach (var library in libraries.EnumerateArray())
                    {
                        if (!library.TryGetProperty("name", out var libName)) continue;
                        var pieces = libName.GetString()!.Split(':');
                        if (pieces.Length < 3) continue;
                        if (pieces[0] == "net.fabricmc" && pieces[1] == "fabric-loader") { loader = LoaderKind.Fabric; loaderVersion = pieces[2]; }
                        else if (pieces[0] == "net.minecraftforge" && pieces[1] == "forge") { loader = LoaderKind.Forge; loaderVersion = pieces[2].StartsWith(mc + "-", StringComparison.Ordinal) ? pieces[2][(mc.Length + 1)..] : pieces[2]; }
                        else if (pieces[0] == "net.neoforged" && pieces[1] == "neoforge") { loader = LoaderKind.NeoForge; loaderVersion = pieces[2]; }
                    }
                profiles.Add((mc, loader, loaderVersion));
            }
            var choices = profiles.Distinct().ToArray();
            if (choices.Count(x => x.Loader != LoaderKind.Vanilla) == 1) choices = choices.Where(x => x.Loader != LoaderKind.Vanilla).ToArray();
            info = info with { RootPrefix = prefix, PayloadPrefix = payload, Notes = "只导入游戏内容；旧运行库、版本文件和账号配置不会导入，运行环境会重新安装。" };
            if (choices.Length == 1) info = info with { Minecraft = choices[0].Minecraft, DetectedLoader = choices[0].Loader, LoaderVersion = choices[0].Version, Loader = choices[0].Loader + " " + choices[0].Version };
            else info = info with { Notes = info.Notes + " 未找到唯一版本，请手动填写 Minecraft 和 Loader。" };
        }
        Payload(zip, info); // Validate the effective payload before any game download begins.
        return info;
    }

    public static string Normalize(string path) => path.Replace('\\', '/');
    private static async Task<JsonDocument> ReadJson(ZipArchiveEntry entry, CancellationToken ct)
    {
        if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("整合包清单或版本配置过大。");
        await using var stream = entry.Open(); return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
    private static (LoaderKind, string) ParseLoader(string id)
    {
        var split = id.IndexOf('-'); if (split < 1 || split == id.Length - 1) throw new InvalidDataException("Loader 标识无效。");
        return (id[..split] switch { "forge" => LoaderKind.Forge, "fabric" => LoaderKind.Fabric, "neoforge" => LoaderKind.NeoForge, _ => throw new NotSupportedException("不支持的 Loader：" + id[..split]) }, id[(split + 1)..]);
    }
    private static string GameFolder(ZipArchive zip, string prefix)
    {
        foreach (var folder in new[] { ".minecraft/", "minecraft/" })
            if (zip.Entries.Any(x => Normalize(x.FullName).StartsWith(prefix + folder, StringComparison.Ordinal))) return prefix + folder;
        var nested = zip.Entries.Select(x => Normalize(x.FullName)).Where(x => x.StartsWith(prefix, StringComparison.Ordinal))
            .Select(x => x.IndexOf("/.minecraft/", StringComparison.Ordinal) is var index && index >= 0 ? x[..(index + 12)] : null).Where(x => x != null).Distinct().ToArray();
        if (nested.Length > 1) throw new InvalidDataException("压缩包中有多个 .minecraft 目录，请分别导入。");
        if (nested.Length == 1) return nested[0]!;
        return prefix;
    }
    private static string IsolatedGameFolder(ZipArchive zip, string root)
    {
        var gameplay = new[] { "mods", "config", "saves", "resourcepacks", "shaderpacks", "kubejs" };
        var files = zip.Entries.Select(x => Normalize(x.FullName)).Where(x => x.StartsWith(root, StringComparison.Ordinal)).Select(x => x[root.Length..]).ToArray();
        if (files.Any(x => gameplay.Contains(x.Split('/')[0], StringComparer.OrdinalIgnoreCase))) return root;
        var isolated = files.Where(x => x.StartsWith("versions/", StringComparison.Ordinal)).Select(x => x.Split('/')).Where(x => x.Length >= 4 && gameplay.Contains(x[2], StringComparer.OrdinalIgnoreCase)).Select(x => root + "versions/" + x[1] + "/").Distinct().ToArray();
        if (isolated.Length > 1) throw new InvalidDataException("ZIP 包含多个隔离的游戏目录，请只打包需要导入的一个版本。");
        return isolated.FirstOrDefault() ?? root;
    }
    private static readonly string[] ContentFolders = ["mods", "config", "saves", "resourcepacks", "shaderpacks", "kubejs", "scripts", "defaultconfigs", "versions", "libraries", "assets", "runtime", ".minecraft", "minecraft"];
    private static string CommonWrapper(ZipArchive zip)
    {
        var files = zip.Entries.Where(x => !x.FullName.EndsWith('/')).Select(x => Normalize(x.FullName)).ToArray(); var prefix = "";
        for (var depth = 0; depth < 4; depth++)
        {
            var first = files[0][prefix.Length..].Split('/')[0];
            if (ContentFolders.Contains(first, StringComparer.OrdinalIgnoreCase) || files.Any(x => !x.StartsWith(prefix + first + "/", StringComparison.Ordinal))) break;
            prefix += first + "/";
        }
        return prefix;
    }
    public static IReadOnlyList<(ZipArchiveEntry Entry, string Relative)> Payload(ZipArchive zip, PackInfo info)
    {
        var prefixes = info.Format switch
        {
            PackFormat.Modrinth => new[] { info.RootPrefix + "overrides/", info.RootPrefix + "client-overrides/" },
            PackFormat.CurseForge => CurseOverride(zip, info),
            _ => new[] { info.PayloadPrefix }
        };
        var result = new List<(ZipArchiveEntry, string)>();
        foreach (var prefix in prefixes)
            foreach (var entry in zip.Entries.Where(x => !x.FullName.EndsWith('/') && Normalize(x.FullName).StartsWith(prefix, StringComparison.Ordinal)))
            {
                var relative = Normalize(entry.FullName)[prefix.Length..]; var first = relative.Split('/')[0];
                if (new[] { "versions", "libraries", "assets", "runtime", ".mineorbit", ".git", "logs", "crash-reports", "natives", "PCL", "HMCL", "__MACOSX" }.Contains(first, StringComparer.OrdinalIgnoreCase)
                    || new[] { "instance.json", ".pending.json", "mmc-pack.json", "instance.cfg", "manifest.json", "modrinth.index.json", "launcher_profiles.json", "launcher_accounts.json", "accounts.json", "accounts.dpapi", "msal.dpapi", "settings.json", "settings.json.secrets", ".DS_Store" }.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
                if (info.Format == PackFormat.PlainZip && info.PayloadPrefix.Contains("versions/", StringComparison.Ordinal) && relative == info.PayloadPrefix.TrimEnd('/').Split('/')[^1] + ".json") continue;
                PackInstallationService.PayloadPath(Path.GetTempPath(), relative);
                result.Add((entry, relative));
            }
        return result;
    }
    private static string[] CurseOverride(ZipArchive zip, PackInfo info)
    {
        var entry = zip.Entries.Single(x => Normalize(x.FullName) == info.ManifestPath);
        using var stream = entry.Open(); using var json = JsonDocument.Parse(stream);
        var folder = json.RootElement.TryGetProperty("overrides", out var value) ? value.GetString()! : "overrides";
        PathSafety.Child(Path.GetTempPath(), folder);
        return [info.RootPrefix + folder.TrimEnd('/', '\\') + "/"];
    }
}
