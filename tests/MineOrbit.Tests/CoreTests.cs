using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MineOrbit.Core;
using MineOrbit.Infrastructure;
using Xunit;

namespace MineOrbit.Tests;
public sealed class CoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MineOrbit-tests", Guid.NewGuid().ToString("N"));
    public CoreTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    [Theory]
    [InlineData("../escape")][InlineData("a/../../escape")][InlineData("/absolute")][InlineData("C:\\file")][InlineData("mods/x:secret")][InlineData("CON.txt")][InlineData("mods/thing. ")][InlineData("mods/../file")]
    public void RejectUnsafePaths(string path) => Assert.Throws<InvalidDataException>(() => PathSafety.Child(root, path));
    [Fact] public void AcceptNestedPath() => Assert.Equal(Path.Combine(root, "mods", "safe.jar"), PathSafety.Child(root, "mods/safe.jar"));
    [Fact] public async Task InstancesAreIsolatedAndPersist()
    {
        var manager = new InstanceManager(root);
        var a = manager.New("同名", "1.20.1", LoaderKind.Fabric, "0.16.10");
        var b = manager.New("同名", "1.20.1", LoaderKind.Vanilla, "");
        Assert.NotEqual(a.Directory, b.Directory);
        await manager.Save(a); await manager.Save(b);
        a.Name = "新名称"; await manager.Save(a);
        var list = await manager.List(); Assert.Equal(2, list.Count); Assert.Contains(list, x => x.Name == "新名称");
        var saves = Path.Combine(a.Directory, "saves"); Directory.CreateDirectory(saves); File.WriteAllText(Path.Combine(saves, "world"), "retain");
        manager.RemoveConfiguration(a); Assert.Single(await manager.List()); Assert.True(File.Exists(Path.Combine(saves, "world")));
    }
    [Fact] public async Task RejectTamperedInstancePath()
    {
        var manager = new InstanceManager(root); var instance = manager.New("a", "1.20.1", LoaderKind.Vanilla, "");
        instance.Directory = root; await Assert.ThrowsAsync<InvalidDataException>(() => manager.Save(instance));
    }
    [Fact] public async Task SettingsRoundTripAndValidation()
    {
        var service = new SettingsService(Path.Combine(root, "settings.json"));
        var settings = new LauncherSettings { DataRoot = root, MaximumRamMb = 8192 }; await service.Save(settings);
        Assert.Equal(8192, (await service.Load()).MaximumRamMb);
        settings.MinimumRamMb = 9000; await Assert.ThrowsAsync<ArgumentException>(() => service.Save(settings));
        Assert.Equal(8192, (await service.Load()).MaximumRamMb);
    }
    private string Zip(params (string name, string text)[] files)
    {
        var path = Path.Combine(root, Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        return path;
    }
    [Fact] public async Task RecognizeMrpack()
    {
        var path = Zip(("modrinth.index.json", """{"formatVersion":1,"game":"minecraft","dependencies":{"minecraft":"1.20.1","fabric-loader":"0.16.10"}}"""));
        var info = await new ModpackImportService().Inspect(path, default);
        Assert.Equal("Modrinth MRPACK", info.Kind); Assert.Equal("1.20.1", info.Minecraft); Assert.Contains("fabric", info.Loader);
    }
    [Fact] public async Task RecognizeCurseForge()
    {
        var info = await new ModpackImportService().Inspect(Zip(("manifest.json", """{"minecraft":{"version":"1.20.1","modLoaders":[{"id":"forge-47.3.0"}]}}""")), default);
        Assert.StartsWith("CurseForge", info.Kind); Assert.Equal("1.20.1", info.Minecraft);
    }
    [Fact] public async Task PlainZipDoesNotGuessVersion()
    {
        var info = await new ModpackImportService().Inspect(Zip(("mods/a.jar", "abc")), default);
        Assert.Null(info.Minecraft); Assert.Null(info.Loader);
    }
    [Fact] public async Task ZipSlipIsRejected() => await Assert.ThrowsAsync<InvalidDataException>(() => new ModpackImportService().Inspect(Zip(("../escape", "danger")), default));
    [Fact] public async Task DuplicatePathsAreRejected() => await Assert.ThrowsAsync<InvalidDataException>(() => new ModpackImportService().Inspect(Zip(("mods/a.jar", "a"), ("MODS/a.jar", "b")), default));
    [Fact] public async Task UnsupportedMrpackIsRejected() => await Assert.ThrowsAsync<InvalidDataException>(() => new ModpackImportService().Inspect(Zip(("modrinth.index.json", """{"formatVersion":2,"game":"minecraft"}""")), default));
    [Fact] public void DpapiRoundTripIsNotPlaintext()
    {
        var path = Path.Combine(root, "account.dpapi"); var storage = new DpapiJsonStorage(path);
        storage.Write(JsonNode.Parse("""{"test":"test-value-only"}""")!, null);
        Assert.DoesNotContain("test-value-only", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.Equal("test-value-only", storage.ReadAsJsonNode()!["test"]!.GetValue<string>());
    }
    [Fact] public async Task HashDetectsCorruption()
    {
        var file = Path.Combine(root, "a"); await File.WriteAllTextAsync(file, "abc");
        var hash = Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes("abc")));
        await DownloadManager.Verify(file, hash, default); await File.WriteAllTextAsync(file, "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadManager.Verify(file, hash, default));
    }
    private sealed class FailingHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request }); } }
    [Fact] public async Task DownloadFailurePreservesExistingFile()
    {
        var path = Path.Combine(root, "file.jar"); File.WriteAllText(path, "existing");
        using var http = new HttpClient(new FailingHandler());
        await Assert.ThrowsAsync<HttpRequestException>(() => new DownloadManager(http).Download(new Uri("https://example.invalid/a"), path, new string('0',128), 1024, null, default));
        Assert.Equal("existing", File.ReadAllText(path)); Assert.Empty(Directory.GetFiles(root, "*.part"));
    }
    [Fact] public async Task DownloadCancellationLeavesNoPartial()
    {
        using var ct = new CancellationTokenSource(); ct.Cancel(); using var http = new HttpClient(new FailingHandler());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DownloadManager(http).Download(new Uri("https://example.invalid/a"), Path.Combine(root,"file"), new string('0',128), 1024, null, ct.Token));
        Assert.Empty(Directory.GetFiles(root));
    }
}
