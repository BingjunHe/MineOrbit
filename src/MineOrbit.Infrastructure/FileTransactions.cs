using MineOrbit.Core;

namespace MineOrbit.Infrastructure;
public static class FileTransactions
{
    private sealed record Entry(string Relative, bool Existed, int Index);
    private sealed record Journal(List<Entry> Entries, bool Committed = false);
    public static void Recover(string root)
    {
        var tx = Path.Combine(root, ".mineorbit", "transaction");
        var journalPath = Path.Combine(tx, "journal.json");
        if (!File.Exists(journalPath)) return;
        GameDirectoryService.EnsureNoLinks(tx);
        var journal = JsonStore.Read<Journal>(journalPath).GetAwaiter().GetResult() ?? throw new InvalidDataException("无法读取恢复日志。");
        if (!journal.Committed)
            foreach (var entry in journal.Entries.AsEnumerable().Reverse())
            {
                var target = PathSafety.Child(root, entry.Relative);
                GameDirectoryService.EnsureNoLinks(Path.GetDirectoryName(target)!);
                var backup = Path.Combine(tx, entry.Index + ".backup");
                if (File.Exists(backup)) File.Move(backup, target, true);
                else if (!entry.Existed && File.Exists(target)) File.Delete(target);
            }
        Directory.Delete(tx, true);
    }
    public static async Task Commit(string root, IReadOnlyDictionary<string, string?> files, CancellationToken ct)
    {
        Recover(root);
        var tx = Path.Combine(root, ".mineorbit", "transaction"); Directory.CreateDirectory(tx);
        var entries = files.Keys.Select((name, index) =>
        {
            var target = PathSafety.Child(root, name); GameDirectoryService.EnsureNoLinks(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("目标文件是链接。");
            return new Entry(name, File.Exists(target), index);
        }).ToList();
        var journal = new Journal(entries);
        await JsonStore.Write(Path.Combine(tx, "journal.json"), journal, ct);
        try
        {
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = PathSafety.Child(root, entry.Relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (entry.Existed) File.Move(target, Path.Combine(tx, entry.Index + ".backup"));
                if (files[entry.Relative] is { } staged) File.Move(staged, target);
            }
            await JsonStore.Write(Path.Combine(tx, "journal.json"), journal with { Committed = true }, CancellationToken.None);
        }
        catch { Recover(root); throw; }
        Recover(root);
    }
}
