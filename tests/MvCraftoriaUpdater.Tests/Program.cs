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
    var liveConfig = new UpdaterConfiguration
    {
        ProductName = "MV Craftoria",
        Repository = "KazzKamikaze/mv-craftoria-release"
    };
    using var liveClient = new GitHubReleaseClient(liveConfig);
    var releases = await liveClient.GetVerifiedReleasesAsync(CancellationToken.None);
    var release = releases.First(item => item.Manifest.Version == "1.0.0-final");
    Assert(release.Manifest.Package.AssetName == "MV-Craftoria-1.0.0.zip", "live package filename");
    Assert(release.Manifest.Package.Size == 1_063_189_726, "live package size");
    Console.WriteLine($"MV_UPDATER_LIVE_RELEASE_VERIFIED {release.DisplayName} {release.Manifest.Package.AssetName}");
    return;
}

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
    Assert(installed["installPath"]!.GetValue<string>() == "MV Craftoria 1.0.0", "fresh install path");
    Assert(installed["guid"]!.GetValue<string>() != "template-guid", "fresh GUID");
    Assert(installed["playedCount"]!.GetValue<int>() == 0, "fresh played count");
    var preservedGuid = installed["guid"]!.GetValue<string>();
    installed["playedCount"] = 42;
    installed["timePlayed"] = 9876;
    File.WriteAllText(metadataPath, installed.ToJsonString(JsonDefaults.Options));

    var second = CreatePackage(root, "1.1.0", ["1.0.0"]);
    using (var client = new GitHubReleaseClient(config, new PackageHandler(second.Bytes)))
    {
        var target = new CurseForgeProfile("MV Craftoria 1.0.0", targetPath, "1.0.0", "1.21.1");
        await engine.InstallAsync(target, second.Release, client, null, CancellationToken.None, target.Name);
    }

    var updated = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
    Assert(updated["name"]!.GetValue<string>() == "MV Craftoria 1.0.0", "updated profile name");
    Assert(updated["guid"]!.GetValue<string>() == preservedGuid, "updated GUID preservation");
    Assert(updated["playedCount"]!.GetValue<int>() == 42, "updated played count preservation");
    Assert(updated["timePlayed"]!.GetValue<int>() == 9876, "updated play time preservation");
    Assert(File.ReadAllText(sentinelPath) == "untouched", "non-selected client isolation");
    Assert(File.ReadAllText(Path.Combine(targetPath, "config", "version.txt")) == "1.1.0", "selected client update");
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
    return (bytes, new VerifiedRelease(manifest, new Uri("https://test.invalid/package.zip"), new Uri("https://test.invalid/release"), "test"));
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
