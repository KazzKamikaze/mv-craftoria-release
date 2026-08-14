using System.Text.Json.Serialization;

namespace MvCraftoriaUpdater.Models;

public sealed class UpdaterConfiguration
{
    public string ProductName { get; init; } = "MV Craftoria";
    public string Repository { get; init; } = "";
    public string ManifestAsset { get; init; } = "mv-release.json";
    public string SignatureAsset { get; init; } = "mv-release.sig";
}

public sealed class ReleaseManifest
{
    public int SchemaVersion { get; init; }
    public string Product { get; init; } = "";
    public string Version { get; init; } = "";
    public string PublishedUtc { get; init; } = "";
    public string MinimumUpdaterVersion { get; init; } = "1.0.0";
    public string Summary { get; init; } = "";
    public string[] Changelog { get; init; } = [];
    public string[] SupportedFrom { get; init; } = [];
    public ReleasePackage Package { get; init; } = new();
}

public sealed class ReleasePackage
{
    public string AssetName { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
}

public sealed class PatchManifest
{
    public int SchemaVersion { get; init; }
    public string Product { get; init; } = "";
    public string TargetVersion { get; init; } = "";
    public string[] SupportedFrom { get; init; } = [];
    public PatchFile[] Files { get; init; } = [];
    public string[] Delete { get; init; } = [];
}

public sealed class PatchFile
{
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
}

public sealed class GitHubReleaseResponse
{
    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; init; } = "";

    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = "";

    public bool Draft { get; init; }

    public bool Prerelease { get; init; }

    public GitHubAsset[] Assets { get; init; } = [];
}

public sealed class GitHubAsset
{
    public string Name { get; init; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; init; } = "";

    public long Size { get; init; }
}

public sealed record VerifiedRelease(
    ReleaseManifest Manifest,
    Uri PackageUri,
    Uri ReleasePageUri,
    string ManifestSha256)
{
    public string DisplayName => Manifest.Version;
};

public sealed record CurseForgeProfile(
    string Name,
    string Path,
    string Version,
    string GameVersion)
{
    public string DisplayName => $"{Name}  •  {Version}";
}

public sealed record UpdateProgress(double Percentage, string Stage, string Detail);

public sealed class InstalledState
{
    public string Product { get; init; } = "MV Craftoria";
    public string Version { get; init; } = "";
    public string PreviousVersion { get; init; } = "";
    public string InstalledUtc { get; init; } = "";
    public string BackupPath { get; init; } = "";
}
