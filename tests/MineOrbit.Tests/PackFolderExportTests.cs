using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineOrbit.Core;
using MineOrbit.Infrastructure;
using Xunit;

namespace MineOrbit.Tests;

public sealed class PackFolderExportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "MineOrbit-folder-export-tests", Guid.NewGuid().ToString("N"));
    public PackFolderExportTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private string P(string relative) => Path.GetFullPath(Path.Combine(root, relative));
    private static string Hash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private async Task Text(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, text); }
    private async Task<GameInstance> Fixture()
    {
        var instance = new GameInstance { Name = "Export fixture", MinecraftVersion = "1.20.1", Loader = LoaderKind.Fabric, LoaderVersion = "0.16.10", LaunchVersion = "fabric-fixture", Directory = P("instance") };
        await Text(P("instance/versions/1.20.1/1.20.1.jar"), "game-jar-fixture");
        await Text(P("libraries/test/core/1/core-1.jar"), "library-fixture");
        await Text(P("libraries/test/native/1/native-1-natives-windows-64.jar"), "native-fixture");
        await Text(P("libraries/not-used/other.jar"), "unrelated");
        var objectHash = Hash("sound-fixture");
        await Text(P("assets/objects/" + objectHash[..2] + "/" + objectHash), "sound-fixture");
        var index = JsonSerializer.Serialize(new { objects = new Dictionary<string, object> { ["sounds/test.ogg"] = new { hash = objectHash, size = 13 } }, @virtual = true, map_to_resources = true });
        await Text(P("assets/indexes/fixture.json"), index);
        await Text(P("assets/log_configs/fixture.xml"), "logging-fixture");
        await JsonStore.Write(P("instance/versions/1.20.1/1.20.1.json"), new
        {
            id = "1.20.1", mainClass = "net.minecraft.client.main.Main",
            downloads = new { client = new { sha1 = Hash("game-jar-fixture") } },
            assetIndex = new { id = "fixture", sha1 = Hash(index) }, javaVersion = new { component = "java-runtime-gamma", majorVersion = 17 },
            logging = new { client = new { file = new { id = "fixture.xml", sha1 = Hash("logging-fixture") } } },
            libraries = new object[]
            {
                new { name = "test:core:1", downloads = new { artifact = new { path = "test/core/1/core-1.jar", sha1 = Hash("library-fixture") } } },
                new { name = "test:linux-only:1", rules = new[] { new { action = "allow", os = new { name = "linux" } } } },
                new { name = "test:native:1", natives = new { windows = "natives-windows-${arch}" } }
            }
        });
        await JsonStore.Write(P("instance/versions/fabric-fixture/fabric-fixture.json"), new { id = "fabric-fixture", inheritsFrom = "1.20.1", jar = "1.20.1", mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient", libraries = Array.Empty<object>() });
        await Text(P("instance/mods/example.jar"), "mod-fixture");
        await Text(P("instance/config/test.txt"), "config-fixture");
        await Text(P("instance/instance.json"), "internal-metadata");
        await Text(P("instance/.mineorbit/pack.json"), "internal-pack-metadata");
        await Text(P("instance/launcher_accounts.json"), "private-account-sentinel");
        await Text(P("instance/accounts.dpapi"), "encrypted-account-sentinel");
        await Text(P("runtime/java-runtime-gamma/windows-x64/java-runtime-gamma/bin/javaw.exe"), "runtime-fixture-never-executed");
        return instance;
    }
    private async Task<PackFolderManifest> ExportFixture(string destination = "export")
    {
        var instance = await Fixture();
        return await PackFolderExportService.PrepareFolder(instance, P("libraries"), P("assets"), P("runtime"), P(destination), P(destination), new(), new Progress<WorkProgress>(), default);
    }
    private async Task<string> OfficialRoot(bool store = false)
    {
        var file = P("official/" + (store ? "launcher_profiles_microsoft_store.json" : "launcher_profiles.json"));
        await Text(file, "{\"profiles\":{\"existing\":{\"name\":\"Existing game\",\"lastVersionId\":\"1.21.1\"}},\"settings\":{\"preserve\":true},\"selectedProfile\":\"existing\",\"authenticationDatabase\":{\"fixture\":\"preserve-this-sentinel\"}}");
        return file;
    }
    [Fact] public async Task FolderContainsRequiredFilesAndNoLauncherCredentials()
    {
        var manifest = await ExportFixture();
        Assert.Equal("fabric-fixture", manifest.LaunchVersion); Assert.Equal("runtime/java/bin/javaw.exe", manifest.JavaExecutable);
        foreach (var path in new[] { "versions/1.20.1/1.20.1.jar", "versions/fabric-fixture/fabric-fixture.json", "libraries/test/core/1/core-1.jar", "libraries/test/native/1/native-1-natives-windows-64.jar", "assets/indexes/fixture.json", "assets/log_configs/fixture.xml", "mods/example.jar", "config/test.txt", "runtime/java/bin/javaw.exe", "assets/virtual/fixture/sounds/test.ogg", "resources/sounds/test.ogg" }) Assert.True(File.Exists(P("export/" + path)), path);
        foreach (var path in new[] { "instance.json", ".mineorbit", "launcher_accounts.json", "accounts.dpapi", "libraries/not-used/other.jar" }) Assert.False(File.Exists(P("export/" + path)) || Directory.Exists(P("export/" + path)), path);
        var profile = JsonNode.Parse(await File.ReadAllTextAsync(P("export/launcher_profiles.json")))!["profiles"]![manifest.ProfileId]!;
        Assert.Equal(P("export"), profile["gameDir"]!.GetValue<string>());
        Assert.Equal(P("export/runtime/java/bin/javaw.exe"), profile["javaDir"]!.GetValue<string>());
        Assert.DoesNotContain("sentinel", await File.ReadAllTextAsync(P("export/launcher_profiles.json")));
        await Text(P("instance/mods/example.jar"), "source-changed");
        Assert.Equal("mod-fixture", await File.ReadAllTextAsync(P("export/mods/example.jar")));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task OfficialRegistrationPreservesProfilesAndBacksUpExactBytes(bool store)
    {
        var manifest = await ExportFixture(); var file = await OfficialRoot(store); var original = await File.ReadAllBytesAsync(file);
        await Text(P("official/versions/1.20.1/1.20.1.json"), "keep-original-official-version");
        var registered = await new OfficialLauncherRegistrationService(() => false).Register(P("export"), P("official"), new Progress<WorkProgress>(), default);
        Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(registered.Backups)));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        Assert.Equal("Existing game", json["profiles"]!["existing"]!["name"]!.GetValue<string>());
        Assert.True(json["settings"]!["preserve"]!.GetValue<bool>());
        Assert.Equal("preserve-this-sentinel", json["authenticationDatabase"]!["fixture"]!.GetValue<string>());
        Assert.Equal("existing", json["selectedProfile"]!.GetValue<string>());
        var entry = json["profiles"]![manifest.ProfileId]!;
        Assert.Equal(P("export"), entry["gameDir"]!.GetValue<string>());
        Assert.Equal(registered.VersionId, entry["lastVersionId"]!.GetValue<string>());
        var child = JsonNode.Parse(await File.ReadAllTextAsync(P($"official/versions/{registered.VersionId}/{registered.VersionId}.json")))!;
        var parent = child["inheritsFrom"]!.GetValue<string>();
        Assert.StartsWith(manifest.ProfileId, parent); Assert.Equal(parent, child["jar"]!.GetValue<string>());
        Assert.True(File.Exists(P($"official/versions/{parent}/{parent}.jar")));
        Assert.Equal("keep-original-official-version", await File.ReadAllTextAsync(P("official/versions/1.20.1/1.20.1.json")));
        Assert.False(Directory.Exists(P("official/mods")));
    }
    [Fact] public async Task RepeatedRegistrationDoesNotDuplicateAndPreservesUserName()
    {
        var manifest = await ExportFixture(); var file = await OfficialRoot(); var service = new OfficialLauncherRegistrationService(() => false);
        var first = await service.Register(P("export"), P("official"), new Progress<WorkProgress>(), default);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        json["profiles"]![manifest.ProfileId]!["name"] = "My customized name";
        await JsonStore.Write(file, json);
        var second = await service.Register(P("export"), P("official"), new Progress<WorkProgress>(), default);
        Assert.Equal(first.VersionId, second.VersionId);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        Assert.Equal(2, saved["profiles"]!.AsObject().Count);
        Assert.Equal("My customized name", saved["profiles"]![manifest.ProfileId]!["name"]!.GetValue<string>());
    }
    [Fact] public async Task DependencyConflictDoesNotChangeOfficialConfiguration()
    {
        await ExportFixture(); var file = await OfficialRoot(); var original = await File.ReadAllBytesAsync(file);
        await Text(P("official/libraries/test/core/1/core-1.jar"), "different-local-file");
        await Assert.ThrowsAsync<IOException>(() => new OfficialLauncherRegistrationService(() => false).Register(P("export"), P("official"), new Progress<WorkProgress>(), default));
        Assert.Equal(original, await File.ReadAllBytesAsync(file));
        Assert.False(Directory.Exists(P("official/versions")));
        Assert.Equal("different-local-file", await File.ReadAllTextAsync(P("official/libraries/test/core/1/core-1.jar")));
    }
    [Fact] public async Task InvalidConfigurationIsNotOverwritten()
    {
        await ExportFixture(); await Text(P("official/launcher_profiles.json"), "invalid-json");
        await Assert.ThrowsAsync<InvalidDataException>(() => new OfficialLauncherRegistrationService(() => false).Register(P("export"), P("official"), new Progress<WorkProgress>(), default));
        Assert.Equal("invalid-json", await File.ReadAllTextAsync(P("official/launcher_profiles.json")));
    }
    [Fact] public async Task ExportedFolderCanBeMovedAndRegisteredAgain()
    {
        var manifest = await ExportFixture(); await OfficialRoot();
        Directory.Move(P("export"), P("moved"));
        await new OfficialLauncherRegistrationService(() => false).Register(P("moved"), P("official"), new Progress<WorkProgress>(), default);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(P("official/launcher_profiles.json")))!;
        Assert.Equal(P("moved"), json["profiles"]![manifest.ProfileId]!["gameDir"]!.GetValue<string>());
        Assert.Equal(P("moved/runtime/java/bin/javaw.exe"), json["profiles"]![manifest.ProfileId]!["javaDir"]!.GetValue<string>());
    }
    [Fact] public async Task MissingBaseJarAndCorruptAssetsFailBeforeSuccess()
    {
        var instance = await Fixture(); File.Delete(P("instance/versions/1.20.1/1.20.1.jar"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => PackFolderExportService.PrepareFolder(instance, P("libraries"), P("assets"), P("runtime"), P("missing-jar"), P("missing-jar"), new(), new Progress<WorkProgress>(), default));
        await Text(P("instance/versions/1.20.1/1.20.1.jar"), "game-jar-fixture");
        var hash = Hash("sound-fixture"); await Text(P("assets/objects/" + hash[..2] + "/" + hash), "corrupted");
        await Assert.ThrowsAsync<InvalidDataException>(() => PackFolderExportService.PrepareFolder(instance, P("libraries"), P("assets"), P("runtime"), P("bad-assets"), P("bad-assets"), new(), new Progress<WorkProgress>(), default));
    }
    [Fact] public async Task NonEmptyDestinationIsRejectedBeforeNetworkOrDeletion()
    {
        await Text(P("destination/world.dat"), "preserve-world");
        Assert.Throws<IOException>(() => PackFolderExportService.ValidateDestination(P("destination")));
        Assert.Equal("preserve-world", await File.ReadAllTextAsync(P("destination/world.dat")));
    }
    [Fact] public async Task RunningLauncherPreventsRegistrationBeforeChanges()
    {
        await ExportFixture(); var file = await OfficialRoot(); var original = await File.ReadAllBytesAsync(file);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OfficialLauncherRegistrationService(() => true).Register(P("export"), P("official"), new Progress<WorkProgress>(), default));
        Assert.Equal(original, await File.ReadAllBytesAsync(file)); Assert.False(Directory.Exists(P("official/versions")));
    }
    [Fact] public async Task CancelledRegistrationRollsBackNewFilesAndPreservesProfile()
    {
        await ExportFixture(); var file = await OfficialRoot(); var original = await File.ReadAllBytesAsync(file);
        using var cancel = new CancellationTokenSource();
        var progress = new ImmediateProgress(message => { if (message.Message.StartsWith("正在添加运行依赖", StringComparison.Ordinal)) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficialLauncherRegistrationService(() => false).Register(P("export"), P("official"), progress, cancel.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(file));
        Assert.Empty(Directory.EnumerateFiles(P("official/versions"), "*", SearchOption.AllDirectories));
    }
    private sealed class ImmediateProgress(Action<WorkProgress> report) : IProgress<WorkProgress> { public void Report(WorkProgress value) => report(value); }
    [Fact] public async Task GeneratedLibraryWithoutHashAndX86OnlyRulesAreHandled()
    {
        var instance = await Fixture();
        await Text(P("libraries/test/generated/1/generated-1.jar"), "generated-fixture");
        var file = P("instance/versions/fabric-fixture/fabric-fixture.json"); var json = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        json["libraries"] = new JsonArray(JsonSerializer.SerializeToNode(new { name = "test:generated:1", downloads = new { artifact = new { path = "test/generated/1/generated-1.jar", sha1 = "" } } }),
            JsonSerializer.SerializeToNode(new { name = "test:x86-only:1", rules = new[] { new { action = "allow", os = new { name = "windows", arch = "x86" } } } }));
        await JsonStore.Write(file, json);
        await PackFolderExportService.PrepareFolder(instance, P("libraries"), P("assets"), P("runtime"), P("generated-export"), P("generated-export"), new(), new Progress<WorkProgress>(), default);
        Assert.True(File.Exists(P("generated-export/libraries/test/generated/1/generated-1.jar")));
        Assert.False(Directory.Exists(P("generated-export/libraries/test/x86-only")));
    }
    [Fact] public async Task CancelledExportLeavesEmptyChosenFolderAndNoPublishedFiles()
    {
        var archive = P("fixture.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("config/test.txt").Open()); writer.Write("fixture"); }
        Directory.CreateDirectory(P("destination"));
        using var http = new HttpClient(new RejectNetwork()); var settings = new LauncherSettingsAccessor(new() { DataRoot = P("data") });
        var installer = new MinecraftInstallationService(new(http), new(), settings);
        var importer = new PackInstallationService(new(), installer, new(http, settings), http, settings);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PackFolderExportService(importer, settings).Export(archive, new("fixture", "1.20.1", LoaderKind.Vanilla, ""), P("destination"), new Progress<WorkProgress>(), cancel.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(P("destination")));
        Assert.Empty(Directory.EnumerateDirectories(root, ".mineorbit-export-*"));
    }
    private sealed class RejectNetwork : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("Unit test must not access network"); }
    [Fact] public async Task VersionCycleAndUnsafeRuntimeMetadataAreRejected()
    {
        var manifest = await ExportFixture();
        await JsonStore.Write(P("export/" + PackFolderExportService.ManifestName), manifest with { JavaExecutable = "../outside/javaw.exe" });
        await Assert.ThrowsAsync<InvalidDataException>(() => PackFolderExportService.ReadManifest(P("export")));
        await JsonStore.Write(P("export/versions/1.20.1/1.20.1.json"), new { id = "1.20.1", inheritsFrom = "fabric-fixture" });
        await Assert.ThrowsAsync<InvalidDataException>(() => ExportFiles.VersionChain(P("export"), "fabric-fixture", default));
    }
}
