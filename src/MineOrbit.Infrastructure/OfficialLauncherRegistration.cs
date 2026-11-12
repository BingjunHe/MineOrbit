using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineOrbit.Core;

namespace MineOrbit.Infrastructure;

public sealed record LauncherRegistrationResult(string VersionId, string[] ProfileFiles, string[] Backups);
public sealed class OfficialLauncherRegistrationService(Func<bool>? launcherRunning = null)
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    public async Task<LauncherRegistrationResult> Register(string exportedDirectory, string launcherDirectory, IProgress<WorkProgress> progress, CancellationToken ct)
    {
        var source = Path.GetFullPath(exportedDirectory); var root = Path.GetFullPath(launcherDirectory);
        if (source.Equals(root, StringComparison.OrdinalIgnoreCase) || root.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("导出文件夹与官方启动器数据目录必须分开，不能互相包含。");
        GameDirectoryService.EnsureNoLinks(root);
        var manifest = await PackFolderExportService.ReadManifest(source, ct);
        var chain = await ExportFiles.VersionChain(source, manifest.LaunchVersion, ct);
        var profileNames = new[] { "launcher_profiles.json", "launcher_profiles_microsoft_store.json" }.Where(x => File.Exists(Path.Combine(root, x))).ToArray();
        if (profileNames.Length == 0) throw new InvalidOperationException("没有找到官方启动器配置。请先正常打开并关闭官方启动器，或选择正确的 .minecraft 数据目录。");
        EnsureClosed(root);
        await using var lease = new FileStream(Path.Combine(root, ".mineorbit-registration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var originals = new Dictionary<string, byte[]>();
        var updates = new Dictionary<string, byte[]>();
        var ids = chain.Select((x, i) => (x.Id, NewId: manifest.ProfileId + "-v" + i)).ToDictionary(x => x.Id, x => x.NewId, StringComparer.OrdinalIgnoreCase);
        var launchId = ids[manifest.LaunchVersion];
        foreach (var name in profileNames)
        {
            var file = Path.Combine(root, name); ExportFiles.RequireRegularFile(file);
            if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("启动器配置过大，请检查后重试。");
            var bytes = await File.ReadAllBytesAsync(file, ct);
            JsonObject json;
            try { json = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) as JsonObject ?? throw new InvalidDataException("官方启动器配置无效，未修改任何启动项。"); }
            catch (JsonException) { throw new InvalidDataException("官方启动器配置 JSON 无效，原文件已保留。请先修复配置，再重试添加。"); }
            if (json["profiles"] != null && json["profiles"] is not JsonObject) throw new InvalidDataException("官方启动器 profiles 格式无效。");
            var profiles = json["profiles"] as JsonObject ?? new JsonObject();
            if (json["profiles"] == null) json["profiles"] = profiles;
            var entry = ExportFiles.LauncherProfile(manifest, source, launchId);
            if (profiles[manifest.ProfileId] is JsonObject previous)
            {
                foreach (var key in new[] { "name", "icon", "created", "lastUsed" })
                    if (previous[key] != null) entry[key] = previous[key]!.DeepClone();
            }
            profiles[manifest.ProfileId] = entry;
            originals[name] = bytes; updates[name] = JsonSerializer.SerializeToUtf8Bytes(json, new JsonSerializerOptions { WriteIndented = true });
        }
        var work = PathSafety.Child(root, ".mineorbit-registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var plan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>(); var replaced = new List<string>(); var backups = new List<string>();
        try
        {
            // Namespace the entire inheritance and JAR chain, preserving all existing versions.
            foreach (var (id, original) in chain)
            {
                var json = (JsonObject)original.DeepClone(); var mapped = ids[id];
                json["id"] = mapped;
                if (json["inheritsFrom"]?.GetValue<string>() is { } parent) json["inheritsFrom"] = ids[parent];
                if (json["jar"]?.GetValue<string>() is { } jar) json["jar"] = ids[jar];
                var relative = $"versions/{mapped}/{mapped}.json";
                var staged = PathSafety.Child(work, relative); await JsonStore.Write(staged, json, ct); plan[relative] = staged;
                var originalJar = PathSafety.Child(source, $"versions/{id}/{id}.jar");
                if (File.Exists(originalJar)) plan[$"versions/{mapped}/{mapped}.jar"] = originalJar;
            }
            foreach (var folder in new[] { "libraries", "assets" })
            {
                var from = Path.Combine(source, folder);
                if (!Directory.Exists(from)) continue;
                foreach (var file in ExportFiles.FilesUnder(from)) plan[folder + "/" + Path.GetRelativePath(from, file).Replace('\\', '/')] = file;
            }
            progress.Report(new("正在检查官方启动器的运行库和资源，已有文件将保留…"));
            var newFiles = new Dictionary<string, string>();
            foreach (var (relative, file) in plan)
            {
                ct.ThrowIfCancellationRequested(); ExportFiles.RequireRegularFile(file);
                var target = PathSafety.Child(root, relative); GameDirectoryService.EnsureNoLinks(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    ExportFiles.RequireRegularFile(target);
                    if (!await ExportFiles.SameFile(file, target, ct)) throw new IOException("官方目录已有不同内容的依赖，未覆盖原文件：" + relative + "。可继续在其他启动器中使用导出文件夹。");
                }
                else newFiles[relative] = file;
            }
            var requiredSpace = newFiles.Values.Sum(x => new FileInfo(x).Length);
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredSpace + 20L * 1024 * 1024) throw new IOException("官方启动器所在磁盘空间不足。");
            EnsureClosed(root);
            foreach (var (name, original) in originals)
                if (!await Unchanged(original, Path.Combine(root, name), ct)) throw new IOException("官方启动器配置在操作中发生变化，请关闭启动器后重试。");
            var counter = 0;
            foreach (var (relative, file) in newFiles)
            {
                var target = PathSafety.Child(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); GameDirectoryService.EnsureNoLinks(Path.GetDirectoryName(target)!);
                var temporary = Path.Combine(work, "file-" + Guid.NewGuid().ToString("N"));
                await ExportFiles.CopyFile(file, temporary, ct);
                File.Move(temporary, target, false); added.Add(target);
                if (++counter == 1 || counter % 100 == 0) progress.Report(new($"正在添加运行依赖 {counter}/{newFiles.Count}", (double)counter / Math.Max(1, newFiles.Count)));
            }
            foreach (var (name, original) in originals)
            {
                ct.ThrowIfCancellationRequested(); EnsureClosed(root);
                var target = Path.Combine(root, name);
                if (!await Unchanged(original, target, ct)) throw new IOException("官方启动器配置已变化，请重试。已添加的本次文件将回滚。");
                var backup = PathSafety.Child(root, ".mineorbit-profile-backups/" + name + "." + Guid.NewGuid().ToString("N") + ".bak");
                await WriteNew(backup, original, ct); backups.Add(backup);
                await Replace(target, updates[name], ct); replaced.Add(name);
            }
            progress.Report(new("已添加官方启动项。打开官方启动器，在 Java 版安装配置中选择“" + manifest.Name + "”。", 1));
            return new(launchId, profileNames.Select(x => Path.Combine(root, x)).ToArray(), backups.ToArray());
        }
        catch
        {
            foreach (var name in replaced.AsEnumerable().Reverse())
            {
                var target = Path.Combine(root, name);
                // Never overwrite edits made by another program during rollback.
                if (File.Exists(target) && await Unchanged(updates[name], target, CancellationToken.None)) await Replace(target, originals[name], CancellationToken.None);
            }
            foreach (var file in added.AsEnumerable().Reverse())
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (File.Exists(file) && plan.TryGetValue(relative, out var original) && await ExportFiles.SameFile(original, file, CancellationToken.None)) File.Delete(file);
            }
            throw;
        }
        finally { if (Directory.Exists(work)) { GameDirectoryService.EnsureNoLinks(work); Directory.Delete(work, true); } }
    }
    private void EnsureClosed(string root)
    {
        if (launcherRunning != null)
        {
            if (launcherRunning()) throw new InvalidOperationException("请先关闭官方 Minecraft 启动器，再添加启动项。");
            return;
        }
        foreach (var name in new[] { "MinecraftLauncher", "Minecraft" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process) if (!process.HasExited) throw new InvalidOperationException("请先关闭官方 Minecraft 启动器，再添加启动项，避免退出时覆盖配置。");
    }
    private static async Task WriteNew(string file, byte[] data, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); GameDirectoryService.EnsureNoLinks(Path.GetDirectoryName(file)!);
        await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true); await output.WriteAsync(data, ct);
    }
    private static async Task<bool> Unchanged(byte[] original, string file, CancellationToken ct)
    {
        var current = await File.ReadAllBytesAsync(file, ct); return original.AsSpan().SequenceEqual(current);
    }
    private static async Task Replace(string file, byte[] data, CancellationToken ct)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await WriteNew(temporary, data, ct); ct.ThrowIfCancellationRequested(); File.Move(temporary, file, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
