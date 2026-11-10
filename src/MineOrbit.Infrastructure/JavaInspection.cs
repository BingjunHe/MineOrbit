using System.Diagnostics;
using System.Text.RegularExpressions;
using CmlLib.Core.Version;

namespace MineOrbit.Infrastructure;
public sealed record JavaCandidate(string Path, string Version, bool Is64Bit)
{ public override string ToString() => $"Java {Version} · {(Is64Bit ? "x64" : "x86 / 未知")} · {Path}"; }
public sealed partial class JavaRuntimeService
{
    public async Task<JavaCandidate> Inspect(string path, CancellationToken ct)
    {
        var console = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "java.exe");
        if (!File.Exists(console)) console = path;
        using var process = new Process { StartInfo = new ProcessStartInfo(console, "-XshowSettings:properties -version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var standard = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        var output = await standard + "\n" + await errors;
        var match = Regex.Match(output, @"java\.version\s*=\s*([^\s]+)");
        if (process.ExitCode != 0 || !match.Success) throw new InvalidOperationException("无法读取此 Java 的版本信息。");
        return new(path, match.Groups[1].Value, output.Contains("sun.arch.data.model = 64", StringComparison.Ordinal) || output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase));
    }
    public async Task Validate(string path, IVersion gameVersion, CancellationToken ct)
    {
        var candidate = await Inspect(path, ct);
        if (!candidate.Is64Bit) throw new InvalidOperationException("MineOrbit 需要 x64 Java；检测到 x86 或未知架构。");
        var required = gameVersion.GetInheritedProperty(x => x.JavaVersion)?.MajorVersion;
        var actual = candidate.Version.StartsWith("1.", StringComparison.Ordinal) ? candidate.Version.Split('.')[1] : candidate.Version.Split('.')[0];
        if (required != null && actual != required) throw new InvalidOperationException($"该游戏版本声明需要 Java {required}，当前路径为 Java {candidate.Version}。请留空 Java 路径使用官方 Runtime，或选择匹配的 Java。");
    }
    public async Task<IReadOnlyList<JavaCandidate>> Detect(CancellationToken ct)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) paths.Add(System.IO.Path.Combine(javaHome, "bin", "javaw.exe"));
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            try { paths.Add(System.IO.Path.Combine(directory.Trim('"'), "javaw.exe")); } catch (ArgumentException) { }
        foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft" })
        {
            var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), vendor);
            if (!Directory.Exists(directory)) continue;
            foreach (var child in Directory.GetDirectories(directory)) paths.Add(System.IO.Path.Combine(child, "bin", "javaw.exe"));
        }
        var result = new List<JavaCandidate>();
        foreach (var path in paths.Where(File.Exists).Take(20))
        {
            ct.ThrowIfCancellationRequested();
            try { result.Add(await Inspect(path, ct)); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (InvalidOperationException) { }
        }
        return result;
    }
}
