using System.IO.Compression;
using MineOrbit.Core;

namespace MineOrbit.Infrastructure;
public sealed class BackupService(LauncherSettingsAccessor settings, GameDirectoryService directories)
{
    public async Task<string> Create(GameInstance instance, CancellationToken ct)
    {
        await using var lease = await directories.Acquire(instance, ct); var source = directories.Resolve(instance);
        var root = Path.Combine(settings.Value.DataRoot, "backups"); Directory.CreateDirectory(root); GameDirectoryService.EnsureNoLinks(root);
        var target = Path.Combine(root, $"{instance.Id:N}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        try
        {
            using var zip = ZipFile.Open(target + ".part", ZipArchiveMode.Create);
            var metadata = zip.CreateEntry("mineorbit-backup.json");
            await using (var writer = metadata.Open()) await System.Text.Json.JsonSerializer.SerializeAsync(writer, instance, cancellationToken: ct);
            async Task AddDirectory(string folder)
            {
                ct.ThrowIfCancellationRequested(); GameDirectoryService.EnsureNoLinks(folder);
                foreach (var file in Directory.GetFiles(folder)) await AddFile(file);
                foreach (var child in Directory.GetDirectories(folder)) await AddDirectory(child);
            }
            async Task AddFile(string file)
            {
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("不备份文件链接。");
                var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                await using var input = File.OpenRead(file); await using var output = zip.CreateEntry(relative, CompressionLevel.Fastest).Open(); await input.CopyToAsync(output, ct);
            }
            foreach (var folder in new[] { "saves", "mods", "config", "resourcepacks", "shaderpacks", "screenshots" })
                if (Directory.Exists(Path.Combine(source, folder))) await AddDirectory(Path.Combine(source, folder));
            if (File.Exists(Path.Combine(source, "options.txt"))) await AddFile(Path.Combine(source, "options.txt"));
        }
        catch { if (File.Exists(target + ".part")) File.Delete(target + ".part"); throw; }
        File.Move(target + ".part", target); return target;
    }
}
