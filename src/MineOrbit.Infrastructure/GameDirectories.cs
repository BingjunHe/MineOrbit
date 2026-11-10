using System.Security.Cryptography;
using System.Text;
using MineOrbit.Core;

namespace MineOrbit.Infrastructure;
public sealed record ActiveGame(int ProcessId, DateTime StartedUtc);
public sealed class GameDirectoryService(LauncherSettingsAccessor settings)
{
    public string Resolve(GameInstance instance) => instance.Isolation switch
    {
        IsolationMode.Independent => instance.Directory,
        IsolationMode.ByVersion => PathSafety.Child(Path.Combine(settings.Value.DataRoot, "game-data"), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CompatibilityKey(instance))))[..24]),
        IsolationMode.Shared => Path.Combine(settings.Value.DataRoot, "game-data", "shared"),
        _ => throw new InvalidDataException("未知版本隔离模式。")
    };
    public static string CompatibilityKey(GameInstance instance) => $"{instance.MinecraftVersion}|{instance.Loader}|{instance.LoaderVersion}";
    public async Task<FileStream> Acquire(GameInstance instance, CancellationToken ct)
    {
        var root = Resolve(instance);
        Directory.CreateDirectory(root);
        EnsureNoLinks(root);
        var meta = Path.Combine(root, ".mineorbit"); Directory.CreateDirectory(meta); EnsureNoLinks(meta);
        FileStream lease;
        try { lease = new FileStream(Path.Combine(meta, "game.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("该游戏目录正在运行游戏或执行文件操作，请等待结束。"); }
        try
        {
            var runningPath = Path.Combine(meta, "game-process.json");
            var running = await JsonStore.Read<ActiveGame>(runningPath, ct);
            if (running != null)
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(running.ProcessId);
                    if (!process.HasExited && process.StartTime.ToUniversalTime() == running.StartedUtc)
                        throw new InvalidOperationException("该目录中的游戏进程仍在运行；请先正常退出游戏。");
                }
                catch (ArgumentException) { /* Process no longer exists. */ }
                if (File.Exists(runningPath)) File.Delete(runningPath);
            }
            var guard = Path.Combine(meta, "compatibility.json");
            var previous = await JsonStore.Read<string>(guard, ct);
            var key = CompatibilityKey(instance);
            if (previous != null && previous != key && Directory.Exists(Path.Combine(root, "mods")) && Directory.EnumerateFiles(Path.Combine(root, "mods")).Any())
                throw new InvalidOperationException("共享目录已有其他 Minecraft / Loader 的 Mods；请选择独立隔离，避免混装。");
            await JsonStore.Write(guard, key, ct);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    public static void EnsureNoLinks(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current != null) { if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("目标目录不能包含目录链接。"); current = current.Parent; }
    }
}
