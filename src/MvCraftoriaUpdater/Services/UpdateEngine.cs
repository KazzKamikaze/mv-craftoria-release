using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MvCraftoriaUpdater.Models;

namespace MvCraftoriaUpdater.Services;

internal sealed class UpdateEngine
{
    private const int BackupLimit = 3;
    private readonly UpdaterConfiguration configuration;

    internal UpdateEngine(UpdaterConfiguration configuration)
    {
        this.configuration = configuration;
    }

    internal async Task<string> InstallAsync(
        CurseForgeProfile profile,
        VerifiedRelease release,
        GitHubReleaseClient releaseClient,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken,
        string? installedProfileName = null)
    {
        if (MinecraftProcessGuard.IsMinecraftRunning())
        {
            throw new InvalidOperationException("Close Minecraft before installing the update.");
        }
        if (!release.Manifest.SupportedFrom.Contains(profile.Version, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Version {profile.Version} cannot update directly to {release.Manifest.Version}. A repair package is required.");
        }

        var session = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}-{Guid.NewGuid():N}";
        var workingRoot = Path.Combine(WorkDirectoryCleaner.WorkRoot, session);
        var packagePath = Path.Combine(workingRoot, release.Manifest.Package.AssetName);
        var stagedPayload = Path.Combine(workingRoot, "payload");
        Directory.CreateDirectory(workingRoot);

        try
        {
            await releaseClient.DownloadPackageAsync(release, packagePath, progress, cancellationToken);
            progress?.Report(new UpdateProgress(58, "Verifying package", "Checking SHA-256 integrity"));
            var packageHash = await ComputeSha256Async(packagePath, cancellationToken);
            if (!string.Equals(packageHash, release.Manifest.Package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException("The downloaded package checksum does not match the signed release.");
            }

            var patch = await StageAndVerifyPackageAsync(packagePath, stagedPayload, release, progress, cancellationToken);
            var backupRoot = ApplyPatch(
                profile,
                patch,
                stagedPayload,
                release.Manifest.Version,
                installedProfileName ?? profile.Name,
                progress,
                cancellationToken);
            PruneBackups(profile.Path);
            progress?.Report(new UpdateProgress(100, "Update complete", $"Backup: {backupRoot}"));
            return backupRoot;
        }
        finally
        {
            WorkDirectoryCleaner.DeleteDirectory(workingRoot, "update download");
        }
    }

    private async Task<PatchManifest> StageAndVerifyPackageAsync(
        string packagePath,
        string stagedPayload,
        VerifiedRelease release,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = archive.GetEntry("mv-patch.json")
            ?? throw new InvalidDataException("The update package has no patch manifest.");
        PatchManifest patch;
        await using (var stream = manifestEntry.Open())
        {
            patch = await JsonSerializer.DeserializeAsync<PatchManifest>(stream, JsonDefaults.Options, cancellationToken)
                ?? throw new InvalidDataException("The patch manifest is empty.");
        }

        if (patch.SchemaVersion != 1 ||
            !string.Equals(patch.Product, configuration.ProductName, StringComparison.Ordinal) ||
            !string.Equals(patch.TargetVersion, release.Manifest.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The patch metadata does not match the signed release.");
        }
        if (!patch.SupportedFrom.SequenceEqual(release.Manifest.SupportedFrom, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The patch compatibility list does not match the signed release.");
        }

        Directory.CreateDirectory(stagedPayload);
        for (var index = 0; index < patch.Files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = patch.Files[index];
            var normalized = NormalizeRelativePath(file.Path);
            var entry = archive.GetEntry("payload/" + normalized)
                ?? throw new InvalidDataException($"Patch payload is missing: {normalized}");
            if (file.Size >= 0 && entry.Length != file.Size)
            {
                throw new InvalidDataException($"Patch file size mismatch: {normalized}");
            }

            var destination = ResolveSafeChild(stagedPayload, normalized);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var source = entry.Open())
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await source.CopyToAsync(output, cancellationToken);
            }
            var hash = await ComputeSha256Async(destination, cancellationToken);
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException($"Patch file checksum mismatch: {normalized}");
            }

            var percentage = 60 + (index + 1) * 15d / Math.Max(1, patch.Files.Length);
            progress?.Report(new UpdateProgress(percentage, "Verifying files", normalized));
        }
        return patch;
    }

    private string ApplyPatch(
        CurseForgeProfile profile,
        PatchManifest patch,
        string stagedPayload,
        string targetVersion,
        string installedProfileName,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var updateRoot = Path.Combine(profile.Path, ".mv-update");
        var backupRoot = Path.Combine(updateRoot, "backups", $"{timestamp}-{targetVersion}");
        Directory.CreateDirectory(backupRoot);
        var touched = new List<TouchedFile>();
        var freshInstall = string.Equals(profile.Version, "NOT_INSTALLED", StringComparison.OrdinalIgnoreCase);
        var identity = freshInstall ? null : ReadProfileIdentity(profile.Path);

        try
        {
            for (var index = 0; index < patch.Files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelativePath(patch.Files[index].Path);
                var source = ResolveSafeChild(stagedPayload, relative);
                var destination = ResolveSafeChild(profile.Path, relative);
                Backup(destination, backupRoot, relative, touched);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                var temporary = destination + ".mv-update-new";
                try
                {
                    File.Copy(source, temporary, true);
                    File.Move(temporary, destination, true);
                }
                finally
                {
                    TryDeleteTemporaryFile(temporary);
                }
                progress?.Report(new UpdateProgress(
                    76 + (index + 1) * 20d / Math.Max(1, patch.Files.Length + patch.Delete.Length),
                    "Installing files",
                    relative));
            }

            foreach (var deletePath in patch.Delete)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelativePath(deletePath);
                var destination = ResolveSafeChild(profile.Path, relative);
                if (!File.Exists(destination)) continue;
                Backup(destination, backupRoot, relative, touched);
                File.Delete(destination);
            }

            RewriteProfileIdentity(profile.Path, installedProfileName, freshInstall, identity);

            var state = new InstalledState
            {
                Product = configuration.ProductName,
                Version = targetVersion,
                PreviousVersion = profile.Version,
                InstalledUtc = DateTimeOffset.UtcNow.ToString("O"),
                BackupPath = backupRoot
            };
            Directory.CreateDirectory(updateRoot);
            File.WriteAllText(
                Path.Combine(updateRoot, "state.json"),
                JsonSerializer.Serialize(state, JsonDefaults.Options),
                new UTF8Encoding(false));
            AppLog.Info($"Updated {profile.Path}: {profile.Version} -> {targetVersion}; backup {backupRoot}");
            return backupRoot;
        }
        catch
        {
            RollBack(touched);
            WorkDirectoryCleaner.DeleteDirectory(backupRoot, "failed update backup");
            throw;
        }
    }

    private static ProfileIdentity? ReadProfileIdentity(string profilePath)
    {
        var path = Path.Combine(profilePath, "minecraftinstance.json");
        if (!File.Exists(path)) return null;
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
        if (root is null) return null;
        return new ProfileIdentity(
            root["name"]?.GetValue<string>(),
            root["guid"]?.GetValue<string>(),
            root["installPath"]?.GetValue<string>(),
            root["lastPlayed"]?.DeepClone(),
            root["playedCount"]?.DeepClone(),
            root["timePlayed"]?.DeepClone(),
            root["installDate"]?.DeepClone(),
            root["groupId"]?.DeepClone());
    }

    private static void RewriteProfileIdentity(
        string profilePath,
        string profileName,
        bool freshInstall,
        ProfileIdentity? identity)
    {
        var path = Path.Combine(profilePath, "minecraftinstance.json");
        if (!File.Exists(path)) throw new InvalidDataException("The client package did not create minecraftinstance.json.");
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("The installed CurseForge profile metadata is invalid.");

        root["name"] = profileName;
        root["installPath"] = AbsoluteDirectoryPath(profilePath);
        root["wasNameManuallyChanged"] = true;
        if (freshInstall)
        {
            root["guid"] = Guid.NewGuid().ToString();
            root["playedCount"] = 0;
            root["timePlayed"] = 0;
            root["lastPlayed"] = DateTimeOffset.UtcNow.ToString("O");
            root["installDate"] = DateTimeOffset.UtcNow.ToString("O");
            root["groupId"] = null;
        }
        else if (identity is not null)
        {
            root["name"] = profileName;
            root["guid"] = identity.Guid;
            root["installPath"] = identity.InstallPath ?? Path.GetFileName(profilePath);
            root["lastPlayed"] = identity.LastPlayed?.DeepClone();
            root["playedCount"] = identity.PlayedCount?.DeepClone();
            root["timePlayed"] = identity.TimePlayed?.DeepClone();
            root["installDate"] = identity.InstallDate?.DeepClone();
            root["groupId"] = identity.GroupId?.DeepClone();
        }
        File.WriteAllText(path, root.ToJsonString(JsonDefaults.Options), new UTF8Encoding(false));
    }

    private static string AbsoluteDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;

    private static void Backup(string destination, string backupRoot, string relative, List<TouchedFile> touched)
    {
        var existed = File.Exists(destination);
        var backup = ResolveSafeChild(backupRoot, relative);
        if (existed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(destination, backup, true);
        }
        touched.Add(new TouchedFile(destination, backup, existed));
    }

    private static void RollBack(IEnumerable<TouchedFile> touched)
    {
        foreach (var file in touched.Reverse())
        {
            try
            {
                if (file.Existed && File.Exists(file.Backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
                    File.Copy(file.Backup, file.Destination, true);
                }
                else if (!file.Existed && File.Exists(file.Destination))
                {
                    File.Delete(file.Destination);
                }
            }
            catch (Exception exception)
            {
                AppLog.Error($"Rollback failed for {file.Destination}", exception);
            }
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception)
        {
            AppLog.Error($"Could not remove temporary update file: {path}", exception);
        }
    }

    private static void PruneBackups(string profilePath)
    {
        var root = Path.Combine(profilePath, ".mv-update", "backups");
        if (!Directory.Exists(root)) return;
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories()
                     .OrderByDescending(item => item.CreationTimeUtc)
                     .Skip(BackupLimit))
        {
            WorkDirectoryCleaner.DeleteDirectory(directory.FullName, "expired update backup");
        }
    }

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            throw new InvalidDataException($"Unsafe patch path: {value}");
        }
        var normalized = value.Replace('\\', '/').TrimStart('/');
        if (normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe patch path: {value}");
        }
        return normalized;
    }

    private static string ResolveSafeChild(string root, string relative)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Patch path escapes its root: {relative}");
        }
        return resolved;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record TouchedFile(string Destination, string Backup, bool Existed);
    private sealed record ProfileIdentity(
        string? Name,
        string? Guid,
        string? InstallPath,
        JsonNode? LastPlayed,
        JsonNode? PlayedCount,
        JsonNode? TimePlayed,
        JsonNode? InstallDate,
        JsonNode? GroupId);
}
