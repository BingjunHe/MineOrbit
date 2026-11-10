using CmlLib.Core;
using MineOrbit.Core;

namespace MineOrbit.Infrastructure;
public static class CachePaths
{
    public static string Resolve(LauncherSettings settings, string configured, string leaf) => string.IsNullOrWhiteSpace(configured) ? Path.Combine(settings.DataRoot, leaf) : Path.GetFullPath(configured);
    public static MinecraftPath Minecraft(LauncherSettings settings, string instance)
    {
        var path = new MinecraftPath(instance);
        if (settings.ShareRuntimeAndAssets)
        {
            path.Library = Resolve(settings, settings.LibrariesPath, "libraries");
            path.Assets = Resolve(settings, settings.AssetsPath, "assets");
            path.Runtime = Resolve(settings, settings.RuntimePath, "runtime");
        }
        foreach (var directory in new[] { path.Library, path.Assets, path.Runtime }) GameDirectoryService.EnsureNoLinks(directory);
        return path;
    }
    public static FileStream InstallLease(LauncherSettings settings)
    {
        var config = Path.Combine(settings.DataRoot, "config"); Directory.CreateDirectory(config); GameDirectoryService.EnsureNoLinks(config);
        try { return new FileStream(Path.Combine(config, "installation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("另一个 MineOrbit 正在使用安装缓存，请稍后重试。"); }
    }
}
