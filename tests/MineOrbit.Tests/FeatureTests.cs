using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineOrbit.Core;
using MineOrbit.Infrastructure;
using Xunit;

namespace MineOrbit.Tests;
public sealed class FeatureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MineOrbit-feature-tests", Guid.NewGuid().ToString("N"));
    public FeatureTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    [Fact] public void SharedArtifactsDoNotChangeGameIsolation()
    {
        var settings = new LauncherSettings { DataRoot = root }; var a = Instance(); var b = Instance();
        var pathA = CachePaths.Minecraft(settings, a.Directory); var pathB = CachePaths.Minecraft(settings, b.Directory);
        Assert.Equal(pathA.Assets, pathB.Assets); Assert.Equal(pathA.Library, pathB.Library); Assert.Equal(pathA.Runtime, pathB.Runtime);
        Assert.NotEqual(pathA.BasePath, pathB.BasePath); Assert.NotEqual(pathA.Versions, pathB.Versions);
        settings.ShareRuntimeAndAssets = false; Assert.NotEqual(CachePaths.Minecraft(settings, a.Directory).Assets, CachePaths.Minecraft(settings, b.Directory).Assets);
    }
    private sealed class RejectNetworkHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("Cache test must not access network"); }
    [Fact] public async Task VerifiedCacheIsReusedWithoutNetwork()
    {
        var accessor = new LauncherSettingsAccessor(new() { DataRoot = root });
        var bytes = Encoding.UTF8.GetBytes("verified-cache-fixture"); var hash = Convert.ToHexString(SHA512.HashData(bytes));
        var cached = Path.Combine(root, "cache", "sha512", hash.ToLowerInvariant()); Directory.CreateDirectory(Path.GetDirectoryName(cached)!); await File.WriteAllBytesAsync(cached, bytes);
        using var http = new HttpClient(new RejectNetworkHandler()); var destination = Path.Combine(root, "result.jar");
        await new DownloadManager(http, accessor).Download(new Uri("https://example.invalid/file"), destination, hash, bytes.Length, null, default);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }
    private GameInstance Instance() => new InstanceManager(root).New("Test", "1.20.1", LoaderKind.Fabric, "0.16.10");
    [Fact] public async Task PendingInstallSurvivesRestartAndIsRemovedAfterCommit()
    {
        var manager = new InstanceManager(root); var item = Instance();
        await manager.SavePending(item, default); Assert.Empty(await manager.List());
        var restarted = new InstanceManager(root); Assert.Equal(item.Id, (await restarted.Pending()).Single().Id);
        await restarted.Save(item); Assert.Empty(await restarted.Pending()); Assert.Single(await restarted.List());
    }
    [Fact] public void IsolationModesUseCorrectDirectories()
    {
        var service = new GameDirectoryService(new(new() { DataRoot = root }));
        var a = Instance(); var b = Instance();
        Assert.NotEqual(service.Resolve(a), service.Resolve(b));
        a.Isolation = b.Isolation = IsolationMode.ByVersion; Assert.Equal(service.Resolve(a), service.Resolve(b));
        var c = b with { LoaderVersion = "0.16.9" }; Assert.NotEqual(service.Resolve(a), service.Resolve(c));
        a.Isolation = b.Isolation = IsolationMode.Shared; Assert.Equal(service.Resolve(a), service.Resolve(b));
    }
    [Fact] public async Task SharedDirectoryRejectsIncompatibleMods()
    {
        var service = new GameDirectoryService(new(new() { DataRoot = root })); var a = Instance() with { Isolation = IsolationMode.Shared };
        await using (var lease = await service.Acquire(a, default))
        { Directory.CreateDirectory(Path.Combine(service.Resolve(a), "mods")); File.WriteAllText(Path.Combine(service.Resolve(a), "mods", "test.jar"), "fixture"); }
        var b = a with { MinecraftVersion = "1.21.1" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Acquire(b, default));
    }
    [Fact] public async Task RunningDirectoryCannotBeModified()
    {
        var service = new GameDirectoryService(new(new() { DataRoot = root })); var a = Instance();
        await using var lease = await service.Acquire(a, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Acquire(a, default));
    }
    [Fact] public async Task LiveGameMarkerSurvivesLauncherLockRelease()
    {
        var service = new GameDirectoryService(new(new() { DataRoot = root })); var item = Instance();
        await using (var lease = await service.Acquire(item, default))
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            await JsonStore.Write(Path.Combine(item.Directory, ".mineorbit", "game-process.json"), new ActiveGame(current.Id, current.StartTime.ToUniversalTime()));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Acquire(item, default));
    }
    [Fact] public async Task FileTransactionCommitsAllChanges()
    {
        Directory.CreateDirectory(Path.Combine(root, "mods")); var old = Path.Combine(root, "mods", "old.jar"); File.WriteAllText(old, "old");
        var staged = Path.Combine(root, "new.part"); File.WriteAllText(staged, "new");
        await FileTransactions.Commit(root, new Dictionary<string, string?> { ["mods/old.jar"] = null, ["mods/new.jar"] = staged }, default);
        Assert.False(File.Exists(old)); Assert.Equal("new", File.ReadAllText(Path.Combine(root, "mods", "new.jar")));
        Assert.False(Directory.Exists(Path.Combine(root, ".mineorbit", "transaction")));
    }
    [Fact] public async Task FileTransactionRollsBackOnFailure()
    {
        Directory.CreateDirectory(Path.Combine(root, "mods")); var old = Path.Combine(root, "mods", "old.jar"); File.WriteAllText(old, "original");
        await Assert.ThrowsAsync<FileNotFoundException>(() => FileTransactions.Commit(root, new Dictionary<string, string?> { ["mods/old.jar"] = Path.Combine(root, "missing") }, default));
        Assert.Equal("original", File.ReadAllText(old));
    }
    [Fact] public async Task CopyHasNewIdentityAndRetainsWorld()
    {
        var manager = new InstanceManager(root); var item = Instance(); Directory.CreateDirectory(Path.Combine(item.Directory, "saves"));
        File.WriteAllText(Path.Combine(item.Directory, "saves", "world.dat"), "world"); await manager.Save(item);
        var copy = await manager.Copy(item, default);
        Assert.NotEqual(copy.Id, item.Id); Assert.Equal(IsolationMode.Independent, copy.Isolation);
        Assert.Equal("world", File.ReadAllText(Path.Combine(copy.Directory, "saves", "world.dat")));
    }
    [Fact] public async Task ApiKeyIsEncryptedAndNotInSettingsJson()
    {
        var service = new SettingsService(Path.Combine(root, "settings.json"));
        await service.Save(new() { DataRoot = root, CurseForgeApiKey = "fixture-not-a-real-key" });
        Assert.DoesNotContain("fixture-not-a-real-key", File.ReadAllText(service.ConfigFile));
        Assert.DoesNotContain("fixture-not-a-real-key", Encoding.UTF8.GetString(File.ReadAllBytes(service.ConfigFile + ".secrets")));
        Assert.Equal("fixture-not-a-real-key", (await service.Load()).CurseForgeApiKey);
    }
    private static ModVersion Mod(string project, params ModDependency[] dependencies) => new()
    { Id = project + "-v1", ProjectId = project, Name = project, GameVersions = ["1.20.1"], Loaders = ["fabric"], Dependencies = dependencies };
    private sealed class Catalog(params ModVersion[] versions) : IModCatalog
    {
        public Task<ModVersion> Version(string id, CancellationToken ct) => Task.FromResult(versions.Single(x => x.Id == id));
        public Task<ModVersion[]> Versions(string project, GameInstance instance, CancellationToken ct) => Task.FromResult(versions.Where(x => x.ProjectId == project).ToArray());
    }
    [Fact] public async Task RequiredDependenciesAreOrderedBeforeRoot()
    {
        var dependency = Mod("api"); var selected = Mod("mod", new ModDependency() { ProjectId = "api", Type = "required" }, new ModDependency() { ProjectId = "ignored", Type = "optional" });
        var resolved = await new ModDependencyResolver(new Catalog(dependency)).Resolve(selected, Instance(), default);
        Assert.Equal(new[] { "api", "mod" }, resolved.Select(x => x.ProjectId));
    }
    [Fact] public async Task CyclesAreReported()
    {
        var a = Mod("a", new ModDependency() { ProjectId = "b", Type = "required" }); var b = Mod("b", new ModDependency() { ProjectId = "a", Type = "required" });
        await Assert.ThrowsAsync<InvalidDataException>(() => new ModDependencyResolver(new Catalog(a, b)).Resolve(a, Instance(), default));
    }
    [Fact] public async Task IncompatibleLoaderIsRejected()
    {
        var a = Mod("a") with { Loaders = ["forge"] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ModDependencyResolver(new Catalog()).Resolve(a, Instance(), default));
    }
    [Fact] public async Task PinnedDependencyVersionMismatchIsRejected()
    {
        var a = Mod("a", new ModDependency() { VersionId = "api-v1", Type = "required" }); var api = Mod("api") with { GameVersions = ["1.21.1"] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ModDependencyResolver(new Catalog(api)).Resolve(a, Instance(), default));
    }
    [Fact] public async Task IncompatibleDependencyIsRejected()
    {
        var a = Mod("a", new ModDependency() { ProjectId = "b", Type = "required" }, new ModDependency() { ProjectId = "b", Type = "incompatible" });
        await Assert.ThrowsAsync<InvalidDataException>(() => new ModDependencyResolver(new Catalog(Mod("b"))).Resolve(a, Instance(), default));
    }
    [Fact] public void ModrinthJsonDeserializesActualFieldNames()
    {
        var json = """{"id":"a","project_id":"p","version_number":"1","game_versions":["1.20.1"],"loaders":["fabric"],"dependencies":[{"project_id":"api","dependency_type":"required"}],"files":[]}""";
        var mod = JsonSerializer.Deserialize<ModVersion>(json, ModrinthService.Json)!;
        Assert.Equal("p", mod.ProjectId); Assert.Equal("required", mod.Dependencies[0].Type); Assert.Equal("1", mod.Number);
    }
    [Theory][InlineData("instance.json")][InlineData(".mineorbit/mods.json")][InlineData("versions/x.json")][InlineData("runtime/bin/java.exe")][InlineData("config/run.ps1")]
    public void PacksCannotOverwriteManagedFiles(string path) => Assert.Throws<InvalidDataException>(() => PackInstallationService.PayloadPath(root, path));
    [Fact] public void ParseNeoForgePackDependencies()
    {
        using var json = JsonDocument.Parse("""{"minecraft":"1.21.1","neoforge":"21.1.1"}""");
        var parsed = PackInstallationService.ReadDependencies(json.RootElement); Assert.Equal(LoaderKind.NeoForge, parsed.Loader); Assert.Equal("1.21.1", parsed.Minecraft);
    }
    [Fact] public void UnknownLoaderIsNotGuessed()
    { using var json = JsonDocument.Parse("""{"minecraft":"1.21.1","unknown-loader":"1"}"""); Assert.Throws<NotSupportedException>(() => PackInstallationService.ReadDependencies(json.RootElement)); }
    [Fact] public async Task BackupContainsWorldAndMetadata()
    {
        var item = Instance(); var directories = new GameDirectoryService(new(new() { DataRoot = root }));
        Directory.CreateDirectory(Path.Combine(item.Directory, "saves")); File.WriteAllText(Path.Combine(item.Directory, "saves", "world.dat"), "world");
        var result = await new BackupService(new(new() { DataRoot = root }), directories).Create(item, default);
        using var zip = System.IO.Compression.ZipFile.OpenRead(result);
        Assert.NotNull(zip.GetEntry("mineorbit-backup.json")); Assert.NotNull(zip.GetEntry("saves/world.dat"));
    }
}
