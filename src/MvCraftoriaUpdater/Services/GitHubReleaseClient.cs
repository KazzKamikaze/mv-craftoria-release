using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MvCraftoriaUpdater.Models;

namespace MvCraftoriaUpdater.Services;

internal sealed partial class GitHubReleaseClient : IDisposable
{
    private readonly HttpClient httpClient;
    private readonly UpdaterConfiguration configuration;

    internal GitHubReleaseClient(UpdaterConfiguration configuration, HttpMessageHandler? handler = null)
    {
        this.configuration = configuration;
        httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        httpClient.Timeout = TimeSpan.FromMinutes(5);
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MV-Craftoria-Updater", "1.0.0"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    internal async Task<VerifiedRelease> GetLatestVerifiedReleaseAsync(CancellationToken cancellationToken)
    {
        if (!RepositoryPattern().IsMatch(configuration.Repository))
        {
            throw new InvalidOperationException("The GitHub update repository has not been configured yet.");
        }

        var apiUri = new Uri($"https://api.github.com/repos/{configuration.Repository}/releases/latest");
        using var response = await httpClient.GetAsync(apiUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            JsonDefaults.Options,
            cancellationToken) ?? throw new InvalidDataException("GitHub returned an empty release response.");

        return await VerifyReleaseAsync(release, cancellationToken);
    }

    internal async Task<IReadOnlyList<VerifiedRelease>> GetVerifiedReleasesAsync(CancellationToken cancellationToken)
    {
        if (!RepositoryPattern().IsMatch(configuration.Repository))
        {
            throw new InvalidOperationException("The GitHub update repository has not been configured yet.");
        }

        var apiUri = new Uri($"https://api.github.com/repos/{configuration.Repository}/releases?per_page=100");
        using var response = await httpClient.GetAsync(apiUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var releases = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse[]>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            JsonDefaults.Options,
            cancellationToken) ?? [];

        var verified = new List<VerifiedRelease>();
        foreach (var release in releases.Where(item => !item.Draft))
        {
            try
            {
                verified.Add(await VerifyReleaseAsync(release, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLog.Error($"Release {release.TagName} was skipped because it could not be verified", exception);
            }
        }
        if (verified.Count == 0) throw new InvalidDataException("No verified MV Craftoria releases are available yet.");
        return verified;
    }

    private async Task<VerifiedRelease> VerifyReleaseAsync(
        GitHubReleaseResponse release,
        CancellationToken cancellationToken)
    {
        var manifestAsset = FindAsset(release, configuration.ManifestAsset);
        var signatureAsset = FindAsset(release, configuration.SignatureAsset);
        var manifestBytes = await httpClient.GetByteArrayAsync(new Uri(manifestAsset.BrowserDownloadUrl), cancellationToken);
        var signatureText = await httpClient.GetStringAsync(new Uri(signatureAsset.BrowserDownloadUrl), cancellationToken);
        SignatureVerifier.Verify(manifestBytes, signatureText);

        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestBytes, JsonDefaults.Options)
            ?? throw new InvalidDataException("The signed release manifest is empty.");
        ValidateManifest(manifest);

        var packageAsset = FindAsset(release, manifest.Package.AssetName);
        if (manifest.Package.Size > 0 && packageAsset.Size != manifest.Package.Size)
        {
            throw new InvalidDataException("The GitHub package size does not match the signed manifest.");
        }

        Uri? importPackageUri = null;
        if (manifest.ImportPackage is not null)
        {
            var importAsset = FindAsset(release, manifest.ImportPackage.AssetName);
            if (manifest.ImportPackage.Size > 0 && importAsset.Size != manifest.ImportPackage.Size)
            {
                throw new InvalidDataException("The GitHub CurseForge import package size does not match the signed manifest.");
            }
            importPackageUri = new Uri(importAsset.BrowserDownloadUrl);
        }

        return new VerifiedRelease(
            manifest,
            new Uri(packageAsset.BrowserDownloadUrl),
            importPackageUri,
            new Uri(release.HtmlUrl),
            Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant());
    }

    internal async Task DownloadPackageAsync(
        VerifiedRelease release,
        string destination,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        await DownloadAssetAsync(
            release.PackageUri,
            release.Manifest.Package,
            destination,
            "Downloading update",
            progress,
            cancellationToken);
    }

    internal async Task DownloadImportPackageAsync(
        VerifiedRelease release,
        string destination,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var package = release.Manifest.ImportPackage
            ?? throw new InvalidOperationException("This release does not provide a CurseForge import package.");
        var uri = release.ImportPackageUri
            ?? throw new InvalidOperationException("The CurseForge import package URL is unavailable.");
        await DownloadAssetAsync(uri, package, destination, "Downloading client installer", progress, cancellationToken);
    }

    private async Task DownloadAssetAsync(
        Uri uri,
        ReleasePackage package,
        string destination,
        string stage,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var expectedSize = package.Size;
        var received = 0L;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
        var buffer = new byte[1024 * 128];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            var percentage = expectedSize > 0 ? Math.Min(55, received * 55d / expectedSize) : 20;
            progress?.Report(new UpdateProgress(percentage, stage, FormatBytes(received, expectedSize)));
        }
    }

    public void Dispose() => httpClient.Dispose();

    private GitHubAsset FindAsset(GitHubReleaseResponse release, string assetName) =>
        release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, assetName, StringComparison.Ordinal))
        ?? throw new InvalidDataException($"GitHub release asset is missing: {assetName}");

    private void ValidateManifest(ReleaseManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported release manifest version.");
        if (!string.Equals(manifest.Product, configuration.ProductName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The signed release belongs to a different product.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Package.AssetName))
        {
            throw new InvalidDataException("The signed release manifest is incomplete.");
        }
        if (!Sha256Pattern().IsMatch(manifest.Package.Sha256))
        {
            throw new InvalidDataException("The signed package checksum is invalid.");
        }
        if (manifest.ImportPackage is not null &&
            (string.IsNullOrWhiteSpace(manifest.ImportPackage.AssetName) ||
             !Sha256Pattern().IsMatch(manifest.ImportPackage.Sha256)))
        {
            throw new InvalidDataException("The signed CurseForge import package metadata is invalid.");
        }
        VersionPolicy.EnsureUpdaterVersionSupported(manifest.MinimumUpdaterVersion);
    }

    private static string FormatBytes(long received, long total)
    {
        static string Size(long value) => value >= 1024 * 1024
            ? $"{value / 1024d / 1024d:0.0} MB"
            : $"{value / 1024d:0.0} KB";
        return total > 0 ? $"{Size(received)} of {Size(total)}" : Size(received);
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
