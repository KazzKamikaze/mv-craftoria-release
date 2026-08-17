using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MvCraftoriaUpdater.Models;
using MvCraftoriaUpdater.Services;

if (args.Contains("--verify-live", StringComparer.OrdinalIgnoreCase))
{
    var versionArgument = Array.FindIndex(args, item =>
        string.Equals(item, "--verify-live", StringComparison.OrdinalIgnoreCase));
    var expectedVersion = versionArgument >= 0 && versionArgument + 1 < args.Length
        ? args[versionArgument + 1]
        : "1.0.0-final";
    var liveConfig = new UpdaterConfiguration
    {
        ProductName = "MV Craftoria",
        Repository = "KazzKamikaze/mv-craftoria-release"
    };
    using var liveClient = new GitHubReleaseClient(liveConfig);
    var releases = await liveClient.GetVerifiedReleasesAsync(CancellationToken.None);
    var release = releases.First(item => item.Manifest.Version == expectedVersion);
    Assert(release.Manifest.Package.AssetName == $"MV-Craftoria-{VersionPolicy.Display(expectedVersion)}.zip", "live package filename");
    Assert(release.Manifest.Package.Size > 0, "live package size");
    Assert(release.Manifest.ImportPackage?.AssetName == $"MV-Craftoria-{VersionPolicy.Display(expectedVersion)}-CurseForge-Import.zip", "live import package filename");
    Assert(release.ImportPackageUri is not null, "live import package URL");
    Console.WriteLine($"MV_UPDATER_LIVE_RELEASE_VERIFIED {release.DisplayName} {release.Manifest.Package.AssetName}");
    return;
}

Assert(VersionPolicy.Display("1.0.0-final") == "1.0.0", "legacy final suffix hidden");
Assert(VersionPolicy.DisplayProfileName("MV Craftoria 1.0.0-final") == "MV Craftoria 1.0.0", "legacy profile suffix hidden");
Assert(VersionPolicy.ProfileName("MV Craftoria", "1.1.0-final") == "MV Craftoria 1.1.0", "versioned profile name");
Assert(VersionPolicy.IsSame("1.0.0", "1.0.0-final"), "legacy final version equivalence");

var root = Path.Combine(Path.GetTempPath(), "mv-updater-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var config = new UpdaterConfiguration { ProductName = "MV Craftoria", Repository = "test/test" };
    var engine = new UpdateEngine(config);
    var targetPath = Path.Combine(root, "Instances", "MV Craftoria 1.0.0");
    var sentinelPath = Path.Combine(root, "Instances", "MV Craftoria", "sentinel.txt");
    Directory.CreateDirectory(Path.GetDirectoryName(sentinelPath)!);
    File.WriteAllText(sentinelPath, "untouched");

    var first = CreatePackage(root, "1.0.0", ["NOT_INSTALLED"]);
    using (var client = new GitHubReleaseClient(config, new PackageHandler(first.Bytes)))
    {
        var target = new CurseForgeProfile("MV Craftoria 1.0.0", targetPath, "NOT_INSTALLED", "1.21.1");
        await engine.InstallAsync(target, first.Release, client, null, CancellationToken.None, target.Name);
    }

    var metadataPath = Path.Combine(targetPath, "minecraftinstance.json");
    var installed = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
    Assert(installed["name"]!.GetValue<string>() == "MV Craftoria 1.0.0", "fresh profile name");
    Assert(
        installed["installPath"]!.GetValue<string>() == Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath)) + Path.DirectorySeparatorChar,
        "fresh absolute install path");
    Assert(installed["guid"]!.GetValue<string>() != "template-guid", "fresh GUID");
    Assert(installed["playedCount"]!.GetValue<int>() == 0, "fresh played count");
    Assert(
        DateTimeOffset.TryParse(installed["lastPlayed"]!.GetValue<string>(), out _),
        "fresh CurseForge-compatible last played date");
    using (var database = JsonDocument.Parse($$"""
        [
          {
            "name": "MV Craftoria 1.0.0",
            "installPath": {{JsonSerializer.Serialize(Path.TrimEndingDirectorySeparator(targetPath) + Path.DirectorySeparatorChar)}}
          }
        ]
        """))
    {
        Assert(
            CurseForgeLocator.ContainsRegisteredProfile(database.RootElement, targetPath, "MV Craftoria 1.0.0"),
            "CurseForge agent registration detection");
        Assert(
            !CurseForgeLocator.ContainsRegisteredProfile(database.RootElement, targetPath, "MV Craftoria Wrong"),
            "CurseForge registration name isolation");
    }
    Assert(
        CurseForgeProcessService.ParseExecutableFromCommand("\"C:\\Tools\\CurseForge.exe\" \"%1\"") == @"C:\Tools\CurseForge.exe",
        "quoted CurseForge protocol command parsing");
    Assert(
        CurseForgeProcessService.ParseExecutableFromCommand(@"C:\Tools\CurseForge.exe --open") == @"C:\Tools\CurseForge.exe",
        "unquoted CurseForge protocol command parsing");
    var fakeCurseForge = Path.Combine(root, "CurseForge.exe");
    File.WriteAllBytes(fakeCurseForge, []);
    Assert(CurseForgeProcessService.IsSupportedExecutable(fakeCurseForge), "manual CurseForge executable validation");
    Assert(!CurseForgeProcessService.IsSupportedExecutable(metadataPath), "manual executable rejects non-applications");
    var fakeOverwolf = Path.Combine(root, "OverwolfLauncher.exe");
    File.WriteAllBytes(fakeOverwolf, []);
    Assert(!CurseForgeProcessService.IsSupportedExecutable(fakeOverwolf), "Overwolf is never accepted as CurseForge");
    Assert(!CurseForgeProcessService.IsSupportedLaunchTarget(fakeOverwolf), "generic Overwolf launcher is never accepted");
    var fakeCurseForgeShortcut = Path.Combine(root, "CurseForge.lnk");
    File.WriteAllBytes(fakeCurseForgeShortcut, []);
    Assert(CurseForgeProcessService.IsSupportedLaunchTarget(fakeCurseForgeShortcut),
        "CurseForge shortcut is accepted for standalone and Overwolf editions");
    var fakeOverwolfShortcut = Path.Combine(root, "Overwolf.lnk");
    File.WriteAllBytes(fakeOverwolfShortcut, []);
    Assert(!CurseForgeProcessService.IsSupportedLaunchTarget(fakeOverwolfShortcut),
        "generic Overwolf shortcut is never accepted");
    var standaloneDatabase = Path.Combine(root, "standalone", "MinecraftGameInstance.json");
    var overwolfDatabase = Path.Combine(root, "overwolf", "MinecraftGameInstance.json");
    Directory.CreateDirectory(Path.GetDirectoryName(standaloneDatabase)!);
    Directory.CreateDirectory(Path.GetDirectoryName(overwolfDatabase)!);
    File.WriteAllText(standaloneDatabase,
        $$"""[{"installPath":{{JsonSerializer.Serialize(targetPath)}}}]""");
    var secondRegisteredPath = Path.Combine(root, "Instances", "MV Craftoria Overwolf");
    File.WriteAllText(overwolfDatabase,
        $$"""[{"installPath":{{JsonSerializer.Serialize(secondRegisteredPath)}}}]""");
    var pathsAcrossEditions = CurseForgeLocator.ReadRegisteredProfilePathsFromDatabases(
        [standaloneDatabase, overwolfDatabase]);
    Assert(pathsAcrossEditions.Contains(Path.GetFullPath(targetPath), StringComparer.OrdinalIgnoreCase),
        "standalone registration database is read");
    Assert(pathsAcrossEditions.Contains(Path.GetFullPath(secondRegisteredPath), StringComparer.OrdinalIgnoreCase),
        "Overwolf registration database is read");
    var selectedModdingRoot = Path.Combine(root, "custom-curseforge", "minecraft");
    var selectedInstancesRoot = Path.Combine(selectedModdingRoot, "Instances");
    var selectedProfileRoot = Path.Combine(selectedInstancesRoot, "MV Craftoria");
    Directory.CreateDirectory(selectedProfileRoot);
    File.WriteAllText(Path.Combine(selectedProfileRoot, "minecraftinstance.json"), "{}");
    Assert(CurseForgeLocator.ResolveInstanceRootSelection(selectedModdingRoot) == selectedInstancesRoot,
        "manual modding folder resolves Instances");
    Assert(CurseForgeLocator.ResolveInstanceRootSelection(selectedInstancesRoot) == selectedInstancesRoot,
        "manual Instances folder remains unchanged");
    Assert(CurseForgeLocator.ResolveInstanceRootSelection(selectedProfileRoot) == selectedInstancesRoot,
        "manual profile folder resolves parent Instances");
    Assert(CurseForgeProcessService.IsMaintenanceProcessName("CurseForge"), "CurseForge process detection");
    Assert(CurseForgeProcessService.IsMaintenanceProcessName("CurseForgeApp"), "alternate CurseForge process detection");
    Assert(CurseForgeProcessService.IsMaintenanceProcessName("Curse.Agent.Host"), "CurseForge agent process detection");
    Assert(!CurseForgeProcessService.IsBlockingMaintenanceProcessName("Curse.Agent.Host"),
        "respawned CurseForge agent does not abort maintenance");
    Assert(CurseForgeProcessService.IsBlockingMaintenanceProcessName("CurseForge"),
        "CurseForge desktop process blocks maintenance");
    Assert(!CurseForgeProcessService.IsMaintenanceProcessName("Overwolf"), "Overwolf process exclusion");
    Assert(CurseForgeProcessService.IsHostedCurseForgeWindow("Overwolf", "CurseForge"), "hosted CurseForge window detection");
    Assert(!CurseForgeProcessService.IsHostedCurseForgeWindow("chrome", "CurseForge download page"),
        "browser windows are never treated as hosted CurseForge");
    Assert(!CurseForgeProcessService.IsHostedCurseForgeWindow("Overwolf", "Overwolf"), "generic Overwolf window exclusion");
    var caseInsensitiveStatePath = Path.Combine(targetPath, ".mv-update", "state.json");
    File.WriteAllText(caseInsensitiveStatePath, "{\"Product\":\"MV Craftoria\",\"Version\":\"1.1.0\"}");
    var locator = new CurseForgeLocator();
    Assert(locator.TryReadProfile(targetPath + Path.DirectorySeparatorChar, "MV Craftoria")?.Version == "1.1.0",
        "installed state properties are case-insensitive");
    Assert(locator.TryReadProfile(targetPath, "MV Craftoria")?.Path == Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath)),
        "profile paths are normalized for deduplication");
    var cleanupPath = Path.Combine(root, "partial-download");
    Directory.CreateDirectory(cleanupPath);
    var readOnlyDownload = Path.Combine(cleanupPath, "partial.zip");
    File.WriteAllText(readOnlyDownload, "partial");
    File.SetAttributes(readOnlyDownload, FileAttributes.ReadOnly);
    Assert(WorkDirectoryCleaner.DeleteDirectory(cleanupPath, "test partial download"), "partial download cleanup");
    Assert(!Directory.Exists(cleanupPath), "partial download directory removed");
    var preservedGuid = installed["guid"]!.GetValue<string>();
    installed["playedCount"] = 42;
    installed["timePlayed"] = 9876;
    installed["customProfileData"] = new JsonObject
    {
        ["keepMe"] = true,
        ["machineSpecific"] = "preserved"
    };
    File.WriteAllText(metadataPath, installed.ToJsonString(JsonDefaults.Options));
    var metadataBeforeUpdate = File.ReadAllBytes(metadataPath);
    var distantHorizonsConfig = Path.Combine(targetPath, "config", "DistantHorizons.toml");
    File.WriteAllText(distantHorizonsConfig, "lodChunkRenderDistanceRadius = 321\nuserOwned = true\n");
    var distantHorizonsBeforeUpdate = File.ReadAllBytes(distantHorizonsConfig);
    var instanceDirectoriesBeforeUpdate = Directory.GetDirectories(Path.GetDirectoryName(targetPath)!).Order().ToArray();
    var modsPath = Path.Combine(targetPath, "mods");
    var conflictingJei = Path.Combine(modsPath, "jei-legacy.jar");
    var conflictingTmrv = Path.Combine(modsPath, "toomanyrecipeviewers-legacy.jar");
    var unrelatedPersonalMod = Path.Combine(modsPath, "personal-extra.jar");
    CreateFakeModJar(conflictingJei, "jei");
    CreateFakeModJar(conflictingTmrv, "toomanyrecipeviewers", "jei");
    CreateFakeModJar(unrelatedPersonalMod, "personal_extra");

    var second = CreatePackage(root, "1.1.0", ["1.0.0"]);
    using (var client = new GitHubReleaseClient(config, new PackageHandler(second.Bytes)))
    {
        var target = new CurseForgeProfile("MV Craftoria 1.0.0", targetPath, "1.0.0", "1.21.1");
        await engine.InstallAsync(
            target,
            second.Release,
            client,
            null,
            CancellationToken.None,
            VersionPolicy.ProfileName(config.ProductName, second.Release.Manifest.Version));
    }

    var updated = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
    Assert(updated["name"]!.GetValue<string>() == "MV Craftoria 1.0.0", "in-place update retains profile name");
    Assert(updated["guid"]!.GetValue<string>() == preservedGuid, "updated GUID preservation");
    Assert(updated["playedCount"]!.GetValue<int>() == 42, "updated played count preservation");
    Assert(updated["timePlayed"]!.GetValue<int>() == 9876, "updated play time preservation");
    Assert(updated["customProfileData"]!["keepMe"]!.GetValue<bool>(), "complete CurseForge profile metadata preservation");
    Assert(updated["customProfileData"]!["machineSpecific"]!.GetValue<string>() == "preserved", "machine-specific profile metadata preservation");
    Assert(File.ReadAllBytes(metadataPath).SequenceEqual(metadataBeforeUpdate), "in-place update preserves profile metadata byte-for-byte");
    Assert(Directory.GetDirectories(Path.GetDirectoryName(targetPath)!).Order().SequenceEqual(instanceDirectoriesBeforeUpdate),
        "in-place update does not create another client directory");
    Assert(File.ReadAllText(sentinelPath) == "untouched", "non-selected client isolation");
    Assert(File.ReadAllText(Path.Combine(targetPath, "config", "version.txt")) == "1.1.0", "selected client update");
    Assert(File.ReadAllBytes(distantHorizonsConfig).SequenceEqual(distantHorizonsBeforeUpdate),
        "existing-client update preserves Distant Horizons config byte-for-byte");
    Assert(!File.Exists(conflictingJei), "conflicting JEI provider removed");
    Assert(!File.Exists(conflictingTmrv), "superseded TMRV version removed");
    Assert(!File.Exists(Path.Combine(modsPath, "toomanyrecipeviewers-1.0.0.jar")),
        "renamed previous managed mod removed");
    Assert(File.Exists(Path.Combine(modsPath, "toomanyrecipeviewers-1.1.0.jar")),
        "current managed mod installed");
    Assert(File.Exists(unrelatedPersonalMod), "unrelated personal mod preserved");

    var instanceDirectoriesBeforeRepair = Directory.GetDirectories(Path.GetDirectoryName(targetPath)!).Order().ToArray();
    using (var client = new GitHubReleaseClient(config, new PackageHandler(second.Bytes)))
    {
        var target = new CurseForgeProfile("MV Craftoria 1.1.0", targetPath, "1.1.0", "1.21.1");
        await engine.InstallAsync(
            target,
            second.Release,
            client,
            null,
            CancellationToken.None,
            VersionPolicy.ProfileName(config.ProductName, second.Release.Manifest.Version));
    }

    var repaired = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
    Assert(repaired["name"]!.GetValue<string>() == "MV Craftoria 1.0.0", "same-version repair retains profile name");
    Assert(repaired["guid"]!.GetValue<string>() == preservedGuid, "same-version repair GUID preservation");
    Assert(repaired["playedCount"]!.GetValue<int>() == 42, "same-version repair play count preservation");
    Assert(repaired["customProfileData"]!["machineSpecific"]!.GetValue<string>() == "preserved",
        "same-version repair machine metadata preservation");
    Assert(File.ReadAllBytes(metadataPath).SequenceEqual(metadataBeforeUpdate), "same-version repair preserves profile metadata byte-for-byte");
    Assert(Directory.GetDirectories(Path.GetDirectoryName(targetPath)!).Order().SequenceEqual(instanceDirectoriesBeforeRepair),
        "same-version repair does not create another client directory");
    Assert(File.ReadAllText(Path.Combine(targetPath, "config", "version.txt")) == "1.1.0", "same-version repair reapplies managed files");
    Assert(File.ReadAllBytes(distantHorizonsConfig).SequenceEqual(distantHorizonsBeforeUpdate),
        "same-version repair preserves Distant Horizons config byte-for-byte");
    Console.WriteLine("MV_UPDATER_PROFILE_AND_VERSION_TEST_PASSED");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static (byte[] Bytes, VerifiedRelease Release) CreatePackage(string root, string version, string[] supportedFrom)
{
    var build = Path.Combine(root, "build-" + Guid.NewGuid().ToString("N"));
    var payload = Path.Combine(build, "payload");
    Directory.CreateDirectory(Path.Combine(payload, "config"));
    var metadata = new JsonObject
    {
        ["name"] = "MV Craftoria",
        ["guid"] = "template-guid",
        ["installPath"] = "MV Craftoria",
        ["playedCount"] = 9,
        ["timePlayed"] = 100,
        ["lastPlayed"] = DateTimeOffset.UtcNow.ToString("O"),
        ["installDate"] = DateTimeOffset.UtcNow.ToString("O"),
        ["groupId"] = null,
        ["gameVersion"] = "1.21.1",
        ["manifest"] = new JsonObject { ["version"] = version }
    };
    File.WriteAllText(Path.Combine(payload, "minecraftinstance.json"), metadata.ToJsonString(JsonDefaults.Options));
    File.WriteAllText(Path.Combine(payload, "config", "version.txt"), version);
    File.WriteAllText(Path.Combine(payload, "config", "DistantHorizons.toml"),
        $"lodChunkRenderDistanceRadius = 128\nrelease = {version}\n");
    CreateFakeModJar(
        Path.Combine(payload, "mods", $"toomanyrecipeviewers-{version}.jar"),
        "toomanyrecipeviewers",
        "jei");
    var files = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories).Select(path => new PatchFile
    {
        Path = Path.GetRelativePath(payload, path).Replace('\\', '/'),
        Sha256 = Hash(path),
        Size = new FileInfo(path).Length
    }).ToArray();
    var patch = new PatchManifest
    {
        SchemaVersion = 1,
        Product = "MV Craftoria",
        TargetVersion = version,
        SupportedFrom = supportedFrom,
        Files = files
    };
    File.WriteAllText(Path.Combine(build, "mv-patch.json"), JsonSerializer.Serialize(patch, JsonDefaults.Options), new UTF8Encoding(false));
    var zipPath = Path.Combine(root, $"MV-Craftoria-{version}.zip");
    ZipFile.CreateFromDirectory(build, zipPath, CompressionLevel.Fastest, false);
    var bytes = File.ReadAllBytes(zipPath);
    var manifest = new ReleaseManifest
    {
        SchemaVersion = 1,
        Product = "MV Craftoria",
        Version = version,
        SupportedFrom = supportedFrom,
        Package = new ReleasePackage
        {
            AssetName = Path.GetFileName(zipPath),
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Size = bytes.Length
        }
    };
    return (bytes, new VerifiedRelease(
        manifest,
        new Uri("https://test.invalid/package.zip"),
        null,
        new Uri("https://test.invalid/release"),
        "test"));
}

static void CreateFakeModJar(string path, params string[] modIds)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    var metadata = archive.CreateEntry("META-INF/neoforge.mods.toml");
    using var writer = new StreamWriter(metadata.Open(), new UTF8Encoding(false));
    writer.WriteLine("modLoader = \"javafml\"");
    writer.WriteLine("loaderVersion = \"[1,)\"");
    foreach (var modId in modIds)
    {
        writer.WriteLine("[[mods]]");
        writer.WriteLine($"modId = \"{modId}\"");
        writer.WriteLine("version = \"1.0.0\"");
    }
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
}

sealed class PackageHandler(byte[] package) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(package),
            RequestMessage = request
        });
}
