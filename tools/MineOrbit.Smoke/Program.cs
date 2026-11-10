using MineOrbit.Core;
using MineOrbit.Infrastructure;
using System.IO.Compression;
using System.Text.Json;

var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine("artifacts", "smoke-data"));
if (args.Contains("--login-check") || args.Contains("--launch-check"))
{
    var accessor = new LauncherSettingsAccessor(new() { DataRoot = root });
    var auth = new MicrosoftAuthenticationService(accessor);
    auth.StageChanged += Console.WriteLine;
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
    try
    {
        if (args.Contains("--login-check"))
        {
            var session = await auth.LoginForLaunch(timeout.Token);
            Console.WriteLine("MINECRAFT JAVA ACCOUNT VERIFIED: " + session.Username);
        }
        else
        {
            var instances = new InstanceManager(root);
            var launchFixture = (await instances.List(timeout.Token)).FirstOrDefault() ?? throw new InvalidOperationException("No installed isolated smoke version; run --install first.");
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var java = new JavaRuntimeService();
            var install = new MinecraftInstallationService(new ModLoaderInstallationService(client), java, accessor);
            var launch = new MinecraftLaunchService(auth, java, accessor, new GameDirectoryService(accessor), install);
            launch.RunningProcessChanged += pid => Console.WriteLine(pid.HasValue ? "REAL JAVA GAME PROCESS STARTED: " + pid : "GAME PROCESS EXITED");
            var lastProgress = DateTime.MinValue;
            var launchProgress = new Progress<WorkProgress>(p => { if ((DateTime.UtcNow - lastProgress).TotalSeconds > 3) { lastProgress = DateTime.UtcNow; Console.WriteLine(p.Message); } });
            var exit = await launch.Launch(launchFixture, instances, launchProgress, timeout.Token);
            Console.WriteLine("GAME EXIT CODE: " + exit);
            Environment.ExitCode = exit == 0 ? 0 : 1;
        }
    }
    catch (AuthenticationFailureException ex) { Console.WriteLine("AUTHENTICATION NOT COMPLETED: " + ex.Message); Environment.ExitCode = 2; }
    return;
}
if (args.Contains("--paths")) { foreach (var property in typeof(CmlLib.Core.MinecraftPath).GetProperties()) Console.WriteLine($"{property.Name}: {property.PropertyType} writable={property.CanWrite}"); return; }
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
var loaders = new ModLoaderInstallationService(http);
var catalog = await new MinecraftVersionService().Get(default);
Console.WriteLine($"Live catalog: {catalog.Count} versions; 1.20.1 present: {catalog.Any(x => x.Id == "1.20.1")}");
var fabric = await loaders.GetVersions("1.20.1", LoaderKind.Fabric, default);
Console.WriteLine($"Live Fabric 1.20.1 loaders: {fabric.Count}, first: {fabric.FirstOrDefault()}");
var forge = await loaders.GetVersions("1.20.1", LoaderKind.Forge, default);
Console.WriteLine($"Live Forge 1.20.1 loaders: {forge.Count}, first: {forge.FirstOrDefault()}");
var neo = await loaders.GetVersions("1.21.1", LoaderKind.NeoForge, default);
Console.WriteLine($"Live NeoForge 1.21.1 loaders: {neo.Count}, first: {neo.FirstOrDefault()}");
if (args.Contains("--mods"))
{
    var fixtureManager = new InstanceManager(root);
    var fixture = (await fixtureManager.List()).First() with { Loader = LoaderKind.Fabric, LoaderVersion = "0.20.1", LaunchVersion = "fabric-loader-0.20.1-1.20.1" };
    var accessor = new LauncherSettingsAccessor(new() { DataRoot = root });
    var directories = new GameDirectoryService(accessor);
    var api = new ModCatalogService(new(http), new(http, accessor), accessor); var resolver = new ModDependencyResolver(api);
    var management = new ModManagementService(api, resolver, new DownloadManager(http, accessor), directories);
    var results = await api.Search("iris", fixture, default);
    var project = results.Projects.First(); Console.WriteLine($"LIVE SEARCH: {results.Projects.Length} hits, selected {project.Title}; {results.Note}");
    var selected = (await api.Versions(project.Id, fixture, default)).First();
    var plan = await resolver.Resolve(selected, fixture, default);
    Console.WriteLine("LIVE DEPENDENCIES: " + string.Join(", ", plan.Select(x => x.Name)));
    await management.Install(fixture, selected, new Progress<WorkProgress>(), default);
    var installed = await management.List(fixture, default);
    Console.WriteLine("MOD INSTALL VERIFIED: " + installed.Count);
    var target = installed.First(x => x.ProjectId == project.Id);
    await management.RemoveOrToggle(fixture, target, false, default);
    var disabled = (await management.List(fixture, default)).Single(x => x.ProjectId == project.Id);
    if (!disabled.Disabled) throw new Exception("Toggle failed");
    await management.RemoveOrToggle(fixture, disabled, false, default);
    await management.Install(fixture, selected, new Progress<WorkProgress>(), default);
    await management.RemoveOrToggle(fixture, target, true, default);
    if ((await management.List(fixture, default)).Any(x => x.ProjectId == project.Id)) throw new Exception("Removal failed");
    Console.WriteLine("MOD TOGGLE / VERIFIED REINSTALL / REMOVE VERIFIED");
    return;
}
if (args.Contains("--pack"))
{
    var accessor = new LauncherSettingsAccessor(new() { DataRoot = Path.Combine(root, "pack-tests") });
    var fixtureManager = new InstanceManager(accessor.Value.DataRoot);
    var api = new ModrinthService(http);
    var dummy = new GameInstance { MinecraftVersion = "1.20.1", Loader = LoaderKind.Fabric };
    var version = (await api.Versions("fabric-api", dummy, default)).First(); var file = version.Files.First(x => x.Primary);
    Directory.CreateDirectory(accessor.Value.DataRoot);
    var archive = Path.Combine(accessor.Value.DataRoot, "fixture-" + Guid.NewGuid().ToString("N") + ".mrpack");
    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
    {
        await using (var stream = zip.CreateEntry("wrapped/modrinth.index.json").Open()) await JsonSerializer.SerializeAsync(stream, new
        {
            formatVersion = 1, game = "minecraft", name = "MineOrbit integration fixture", versionId = "test",
            dependencies = new Dictionary<string, string> { ["minecraft"] = "1.20.1", ["fabric-loader"] = "0.20.1" },
            files = new[] { new { path = "mods/" + file.Filename, hashes = file.Hashes, downloads = new[] { file.Url }, fileSize = file.Size } }
        });
        using (var writer = new StreamWriter(zip.CreateEntry("wrapped/overrides/config/mineorbit-test.txt").Open())) writer.Write("base");
        using (var writer = new StreamWriter(zip.CreateEntry("wrapped/client-overrides/config/mineorbit-test.txt").Open())) writer.Write("client");
    }
    using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));
    var installation = new MinecraftInstallationService(loaders, new JavaRuntimeService(), accessor);
    var importer = new PackInstallationService(new ModpackImportService(), installation, new DownloadManager(http, accessor), http, accessor);
    var lastMessage = DateTime.MinValue;
    var packProgress = new Progress<WorkProgress>(p => { if ((DateTime.UtcNow - lastMessage).TotalSeconds > 5) { lastMessage = DateTime.UtcNow; Console.WriteLine(p.Message); } });
    var imported = await importer.Import(archive, new("Integration MRPACK", null, LoaderKind.Vanilla, null), fixtureManager, packProgress, cancel.Token);
    if (File.ReadAllText(Path.Combine(imported.Directory, "config", "mineorbit-test.txt")) != "client") throw new Exception("Override priority failed");
    await DownloadManager.Verify(Path.Combine(imported.Directory, "mods", file.Filename), file.Hashes["sha512"], cancel.Token);
    Console.WriteLine("MRPACK IMPORT VERIFIED: " + imported.LaunchVersion);
    Console.WriteLine("REAL MINECRAFT / FABRIC / FABRIC API / CLIENT OVERRIDES VERIFIED. No login or game launch performed.");
    var plain = Path.Combine(accessor.Value.DataRoot, "plain-" + Guid.NewGuid().ToString("N") + ".zip");
    using (var zip = ZipFile.Open(plain, ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(zip.CreateEntry("wrapped/.minecraft/config/plain-test.txt").Open())) writer.Write("plain");
        using (var writer = new StreamWriter(zip.CreateEntry("wrapped/.minecraft/versions/fabric/fabric.json").Open())) writer.Write("{\"inheritsFrom\":\"1.20.1\",\"libraries\":[{\"name\":\"net.fabricmc:fabric-loader:0.20.1\"}]}");
        using (var writer = new StreamWriter(zip.CreateEntry("wrapped/.minecraft/launcher_accounts.json").Open())) writer.Write("never-import-this-fixture");
    }
    var detected = await new ModpackImportService().Inspect(plain, cancel.Token);
    var plainImported = await importer.Import(plain, new("Integration Wrapped ZIP", detected.Minecraft, detected.DetectedLoader!.Value, detected.LoaderVersion), fixtureManager, packProgress, cancel.Token);
    if (File.ReadAllText(Path.Combine(plainImported.Directory, "config", "plain-test.txt")) != "plain") throw new Exception("Plain ZIP failed");
    if (File.Exists(Path.Combine(plainImported.Directory, "launcher_accounts.json"))) throw new Exception("Account configuration must not be imported");
    Console.WriteLine("WRAPPED ZIP IMPORT VERIFIED with detected Minecraft / Loader and excluded credentials");
    var multi = Path.Combine(accessor.Value.DataRoot, "multimc-" + Guid.NewGuid().ToString("N") + ".zip");
    using (var zip = ZipFile.Open(multi, ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(zip.CreateEntry("instance/mmc-pack.json").Open())) writer.Write("{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.20.1\"},{\"uid\":\"net.fabricmc.fabric-loader\",\"version\":\"0.20.1\"}]}");
        using (var writer = new StreamWriter(zip.CreateEntry("instance/.minecraft/config/multimc-test.txt").Open())) writer.Write("multimc");
    }
    var multiImported = await importer.Import(multi, new("Integration MultiMC ZIP", null, LoaderKind.Vanilla, null), fixtureManager, packProgress, cancel.Token);
    if (File.ReadAllText(Path.Combine(multiImported.Directory, "config", "multimc-test.txt")) != "multimc") throw new Exception("MultiMC import failed");
    Console.WriteLine("MULTIMC ZIP IMPORT VERIFIED: " + multiImported.LaunchVersion);
    return;
}
if (args.Contains("--neo"))
{
    var accessor = new LauncherSettingsAccessor(new() { DataRoot = Path.Combine(root, "neoforge-tests") });
    var fixtureManager = new InstanceManager(accessor.Value.DataRoot);
    var fixture = fixtureManager.New("Integration NeoForge 1.21.1", "1.21.1", LoaderKind.NeoForge, neo.First());
    var neoProgress = new Progress<WorkProgress>();
    using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));
    await new MinecraftInstallationService(loaders, new JavaRuntimeService(), accessor).Install(fixture, fixtureManager, neoProgress, cancel.Token);
    Console.WriteLine("NEOFORGE INSTALL VERIFIED: " + fixture.LaunchVersion); return;
}
if (!args.Contains("--install") && !args.Contains("--forge")) return;
var manager = new InstanceManager(root);
var instance = args.Contains("--forge")
    ? (await manager.List()).First() with { Name = "Integration Forge 1.20.1", Loader = LoaderKind.Forge, LoaderVersion = forge.First() }
    : manager.New("Integration Fabric 1.20.1", "1.20.1", LoaderKind.Fabric, fabric.First());
Console.WriteLine("Isolated installation target: " + instance.Directory);
var last = DateTime.MinValue;
var progress = new Progress<WorkProgress>(p => { if ((DateTime.UtcNow - last).TotalSeconds > 3) { last = DateTime.UtcNow; Console.WriteLine($"{p.Fraction:P0} {p.Message}"); } });
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
var settings = new LauncherSettingsAccessor(new() { DataRoot = root });
await new MinecraftInstallationService(loaders, new JavaRuntimeService(), settings).Install(instance, manager, progress, cancellation.Token);
Console.WriteLine("INSTALL VERIFIED: " + instance.LaunchVersion);
Console.WriteLine("Instance metadata exists: " + File.Exists(Path.Combine(instance.Directory, "instance.json")));
Console.WriteLine("This does not authenticate or launch Minecraft.");
