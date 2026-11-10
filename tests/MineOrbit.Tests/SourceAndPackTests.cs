using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineOrbit.Core;
using MineOrbit.Infrastructure;
using Xunit;

namespace MineOrbit.Tests;

public sealed class SourceAndPackTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MineOrbit-source-pack-tests", Guid.NewGuid().ToString("N"));
    public SourceAndPackTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private static GameInstance Instance => new() { MinecraftVersion = "1.20.1", Loader = LoaderKind.Fabric };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); var response = send(request); response.RequestMessage = request; return Task.FromResult(response); } }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static ModCatalogService Catalog(HttpClient http, LauncherSettings settings) => new(new(http), new(http, new(settings)), new(settings));
    [Theory][InlineData(false, false)][InlineData(true, false)][InlineData(false, true)]
    public async Task AutomaticSearchKeepsAvailableSources(bool mrFails, bool cfFails)
    {
        var hosts = new ConcurrentBag<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var cf = request.RequestUri!.Host == "api.curseforge.com"; hosts.Add(request.RequestUri.Host);
            Assert.Equal(cf, request.Headers.Contains("x-api-key"));
            if (cf ? cfFails : mrFails) return new(HttpStatusCode.ServiceUnavailable);
            return cf ? Json(new { data = new[] { new { id = 42, name = "Same name", summary = "CF fixture" } } })
                : Json(new { hits = new[] { new { project_id = "mr-project", title = "Same name", description = "MR fixture" } } });
        }));
        var result = await Catalog(http, new() { CurseForgeApiKey = "fixture-key" }).Search("test", Instance, default);
        Assert.Equal((mrFails ? 0 : 1) + (cfFails ? 0 : 1), result.Projects.Length);
        Assert.Contains("api.modrinth.com", hosts); Assert.Contains("api.curseforge.com", hosts);
        if (!cfFails) Assert.Contains(result.Projects, x => x.Id == "cf:42" && x.Source == "CurseForge");
        if (!mrFails) Assert.Contains(result.Projects, x => x.Id == "mr-project" && x.Source == "Modrinth");
    }
    [Fact] public async Task UnconfiguredCurseForgeIsSkippedInAutomaticMode()
    {
        using var http = new HttpClient(new Handler(request => { Assert.Equal("api.modrinth.com", request.RequestUri!.Host); return Json(new { hits = Array.Empty<object>() }); }));
        var result = await Catalog(http, new()).Search("test", Instance, default);
        Assert.Contains("未配置", result.Note);
    }
    [Fact] public async Task ExplicitChannelDoesNotSilentlySwitch()
    {
        using var http = new HttpClient(new Handler(request => { Assert.Equal("api.modrinth.com", request.RequestUri!.Host); return new(HttpStatusCode.ServiceUnavailable); }));
        await Assert.ThrowsAsync<HttpRequestException>(() => Catalog(http, new() { ModDownloadSource = ModDownloadSource.Modrinth, CurseForgeApiKey = "fixture" }).Search("test", Instance, default));
    }
    [Fact] public async Task BothSourceFailuresAreReported()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<HttpRequestException>(() => Catalog(http, new() { CurseForgeApiKey = "fixture" }).Search("test", Instance, default));
    }
    [Fact] public async Task LegacyEmptyClientIdUsesMineOrbitApplicationIdentity()
    {
        var path = Path.Combine(root, "legacy.json");
        await File.WriteAllTextAsync(path, "{\"MicrosoftClientId\":\"\"}");
        Assert.Equal(LauncherIdentity.MicrosoftClientId, (await new SettingsService(path).Load()).MicrosoftClientId);
    }
    [Fact] public void CurseForgeMetadataKeepsSourceDependenciesAndHash()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            id = 23, modId = 42, displayName = "Fixture", fileName = "fixture.jar", fileLength = 12,
            downloadUrl = "https://edge.forgecdn.net/files/0/23/fixture.jar", gameVersions = new[] { "1.20.1", "Fabric" },
            hashes = new[] { new { algo = 1, value = new string('a', 40) } },
            dependencies = new[] { new { modId = 43, relationType = 3 }, new { modId = 44, relationType = 5 }, new { modId = 45, relationType = 2 } }
        });
        var version = CurseForgeService.MapVersion(json);
        Assert.Equal("cf:42/23", version.Id); Assert.Contains("fabric", version.Loaders);
        Assert.Equal("cf:43", version.Dependencies.Single(x => x.Type == "required").ProjectId);
        Assert.Equal("cf:44", version.Dependencies.Single(x => x.Type == "incompatible").ProjectId);
        Assert.Equal(2, version.Dependencies.Length); Assert.Equal(new string('a', 40), version.Files.Single().Hashes["sha1"]);
        Assert.Equal(6, CurseForgeService.LoaderId(LoaderKind.NeoForge));
        Assert.Throws<InvalidDataException>(() => CurseForgeService.ProjectId("cf:../../bad"));
    }
    [Theory][InlineData("https://edge.forgecdn.net/files/a.jar", true)][InlineData("https://mediafilez.forgecdn.net/a.jar", true)][InlineData("https://cdn.modrinth.com/a.jar", false)][InlineData("https://forgecdn.net.evil.invalid/a.jar", false)]
    public void DownloadKeysAreLimitedToCurseForgeHosts(string address, bool credentialed)
    {
        using var request = DownloadManager.CreateRequest(new(address), "fixture-key");
        Assert.Equal(credentialed, request.Headers.Contains("x-api-key"));
        Assert.DoesNotContain("fixture-key", request.RequestUri!.ToString());
    }
    [Fact] public async Task DownloadFallbackUsesIdenticalHashNotMatchingName()
    {
        var bytes = Encoding.UTF8.GetBytes("identical official-file fixture"); var sha1 = Convert.ToHexString(SHA1.HashData(bytes));
        var file = new ModFile("fixture.jar", "https://edge.forgecdn.net/missing.jar", new() { ["sha1"] = sha1 }, bytes.Length, true);
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "edge.forgecdn.net") return new(HttpStatusCode.NotFound);
            if (request.RequestUri.Host == "api.modrinth.com") return Json(new ModVersion { Id = "mirror", Files = [file with { Url = "https://cdn.modrinth.com/verified.jar" }] });
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var target = Path.Combine(root, "fixture.jar");
        await Catalog(http, new()).Download(new() { ProjectId = "cf:42" }, file, Instance, target, new(http), new Progress<WorkProgress>(), default);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.False(ModCatalogService.SameFile(file, file with { Hashes = new() { ["sha1"] = new string('0', 40) } }));
    }
    private string Zip(params (string Path, string Content)[] files)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open()); writer.Write(file.Content); }
        return path;
    }
    private const string Mr = "{\"formatVersion\":1,\"game\":\"minecraft\",\"name\":\"Test pack\",\"dependencies\":{\"minecraft\":\"1.20.1\",\"fabric-loader\":\"0.16.10\"},\"files\":[]}";
    [Fact] public async Task WrappedMrpackExtractsClientOverridesLast()
    {
        var path = Zip(("wrapped/modrinth.index.json", Mr), ("wrapped/overrides/config/test.txt", "base"), ("wrapped/client-overrides/config/test.txt", "client"), ("wrapped/server-overrides/config/test.txt", "server"));
        var info = await new ModpackImportService().Inspect(path, default);
        Assert.Equal(PackFormat.Modrinth, info.Format); Assert.Equal("wrapped/", info.RootPrefix); Assert.Equal("Test pack", info.Name);
        Assert.Equal(LoaderKind.Fabric, info.DetectedLoader); Assert.Equal("0.16.10", info.LoaderVersion);
        using var zip = ZipFile.OpenRead(path); var output = Path.Combine(root, "out");
        await PackInstallationService.Extract(ModpackImportService.Payload(zip, info), output, new Progress<WorkProgress>(), default);
        Assert.Equal("client", File.ReadAllText(Path.Combine(output, "config/test.txt")));
        Assert.False(Directory.Exists(Path.Combine(output, "wrapped")));
    }
    [Fact] public async Task WrappedCurseForgeUsesDeclaredOverrideDirectory()
    {
        var path = Zip(("pack/manifest.json", "{\"manifestType\":\"minecraftModpack\",\"manifestVersion\":1,\"minecraft\":{\"version\":\"1.20.1\",\"modLoaders\":[{\"id\":\"forge-47.3.0\",\"primary\":true}]},\"overrides\":\"payload\",\"files\":[]}"), ("pack/payload/config/a.txt", "config"));
        var info = await new ModpackImportService().Inspect(path, default);
        Assert.Equal(LoaderKind.Forge, info.DetectedLoader); Assert.Equal("47.3.0", info.LoaderVersion);
        using var zip = ZipFile.OpenRead(path); Assert.Equal("config/a.txt", ModpackImportService.Payload(zip, info).Single().Relative);
    }
    [Fact] public async Task PrismMultiMcComponentsAndGameDirectoryAreRecognized()
    {
        var path = Zip(("pack/mmc-pack.json", "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"},{\"uid\":\"org.lwjgl3\",\"version\":\"3.3.3\"},{\"uid\":\"net.neoforged\",\"version\":\"21.1.100\"}]}"), ("pack/.minecraft/config/a.txt", "config"), ("pack/instance.cfg", "Name=Ignored commands"));
        var info = await new ModpackImportService().Inspect(path, default);
        Assert.Equal(PackFormat.MultiMC, info.Format); Assert.Equal("pack/.minecraft/", info.PayloadPrefix);
        Assert.Equal("1.21.1", info.Minecraft); Assert.Equal(LoaderKind.NeoForge, info.DetectedLoader);
        using var zip = ZipFile.OpenRead(path); Assert.Single(ModpackImportService.Payload(zip, info));
    }
    [Fact] public async Task CompleteZipDetectsFabricButDoesNotImportCredentialsOrOldRuntime()
    {
        var path = Zip(("pack/.minecraft/versions/fabric/fabric.json", "{\"inheritsFrom\":\"1.20.1\",\"libraries\":[{\"name\":\"net.fabricmc:fabric-loader:0.16.10\"}]}"), ("pack/.minecraft/mods/fixture.jar", "mod"), ("pack/.minecraft/config/a.txt", "config"), ("pack/.minecraft/launcher_accounts.json", "credential-sentinel"), ("pack/.minecraft/runtime/java.exe", "do-not-import"));
        var info = await new ModpackImportService().Inspect(path, default);
        Assert.Equal("1.20.1", info.Minecraft); Assert.Equal(LoaderKind.Fabric, info.DetectedLoader);
        using var zip = ZipFile.OpenRead(path); var payload = ModpackImportService.Payload(zip, info);
        Assert.Equal(2, payload.Count); Assert.Contains(payload, x => x.Relative == "mods/fixture.jar");
        var output = Path.Combine(root, "complete"); await PackInstallationService.Extract(payload, output, new Progress<WorkProgress>(), default);
        Assert.False(File.Exists(Path.Combine(output, "launcher_accounts.json"))); Assert.False(Directory.Exists(Path.Combine(output, "runtime")));
    }
    [Fact] public async Task MultiMcCustomPatchesAreNotSilentlyDiscarded()
    {
        var path = Zip(("mmc-pack.json", "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.20.1\"}]}"), ("patches/net.minecraft.json", "{}"));
        await Assert.ThrowsAsync<NotSupportedException>(() => new ModpackImportService().Inspect(path, default));
    }
    [Theory][InlineData("config/a.txt", "")][InlineData("pack/config/a.txt", "pack/")][InlineData("pack/.minecraft/config/a.txt", "pack/.minecraft/")]
    public async Task PlainZipDoesNotFlattenGameContentFolders(string entry, string expected)
    {
        var path = Zip((entry, "content")); var info = await new ModpackImportService().Inspect(path, default);
        Assert.Equal(expected, info.PayloadPrefix);
        using var zip = ZipFile.OpenRead(path); Assert.Equal("config/a.txt", ModpackImportService.Payload(zip, info).Single().Relative);
    }
    [Fact] public async Task AmbiguousPacksAndExecutablePayloadAreRejectedBeforeInstallation()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new ModpackImportService().Inspect(Zip(("one/modrinth.index.json", Mr), ("two/modrinth.index.json", Mr)), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ModpackImportService().Inspect(Zip(("config/a.txt", "ok"), ("run.bat", "bad")), default));
    }
    [Fact] public async Task WrappedGameFolderIsFoundBesideReadmeAndIsolatedVersionContentIsFlattened()
    {
        var path = Zip(("readme.txt", "outside game"), ("pack/.minecraft/versions/fabric/fabric.json", "{\"inheritsFrom\":\"1.20.1\",\"libraries\":[{\"name\":\"net.fabricmc:fabric-loader:0.16.10\"}]}"), ("pack/.minecraft/versions/fabric/config/test.txt", "config"), ("pack/.minecraft/versions/fabric/mods/test.jar", "mod"));
        var info = await new ModpackImportService().Inspect(path, default);
        Assert.Equal("pack/.minecraft/versions/fabric/", info.PayloadPrefix);
        Assert.Equal("1.20.1", info.Minecraft);
        using var zip = ZipFile.OpenRead(path); var payload = ModpackImportService.Payload(zip, info);
        Assert.Equal(2, payload.Count); Assert.Contains(payload, x => x.Relative == "config/test.txt");
    }
    [Theory][InlineData("OutOfMemoryError", "内存")][InlineData("UnsupportedClassVersionError", "Java")][InlineData("GLFW error 65542", "显卡")][InlineData("Incompatible mod set", "Mod")]
    public void GameExitDiagnosticsAreActionable(string output, string expected) => Assert.Contains(expected, LaunchDiagnostics.Explain(output));
}
