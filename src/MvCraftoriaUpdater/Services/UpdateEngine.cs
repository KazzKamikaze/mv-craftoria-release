using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
        var freshInstall = string.Equals(profile.Version, "NOT_INSTALLED", StringComparison.OrdinalIgnoreCase);
        var profileMetadataPath = Path.Combine(profile.Path, "minecraftinstance.json");
        if (!freshInstall && (!Directory.Exists(profile.Path) || !File.Exists(profileMetadataPath)))
        {
            throw new InvalidDataException("The selected CurseForge client no longer exists. Refresh the client list and select it again.");
        }
        if (freshInstall && Directory.Exists(profile.Path))
        {
            throw new IOException("The new-client destination already exists. Choose another profile name.");
        }
        if (MinecraftProcessGuard.IsMinecraftRunning())
        {
            throw new InvalidOperationException("Close Minecraft before installing the update.");
        }
        var sameVersion = VersionPolicy.IsSame(profile.Version, release.Manifest.Version);
        if (!sameVersion && !release.Manifest.SupportedFrom.Contains(profile.Version, StringComparer.OrdinalIgnoreCase))
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

        if (patch.SchemaVersion is < 1 or > 2 ||
            !string.Equals(patch.Product, configuration.ProductName, StringComparison.Ordinal) ||
            !string.Equals(patch.TargetVersion, release.Manifest.Version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The patch metadata does not match the signed release.");
        }
        if (!patch.SupportedFrom.SequenceEqual(release.Manifest.SupportedFrom, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The patch compatibility list does not match the signed release.");
        }

        ValidatePatchInventory(patch);

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
        if (!freshInstall && string.IsNullOrWhiteSpace(identity?.Guid))
        {
            throw new InvalidDataException("The selected client has no CurseForge identity and cannot be updated safely.");
        }

        try
        {
            for (var index = 0; index < patch.Files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeRelativePath(patch.Files[index].Path);
                if (!freshInstall && IsProfileMetadata(relative)) continue;
                if (!freshInstall && IsUserOwnedPath(relative)) continue;
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
                if (!freshInstall && IsProfileMetadata(relative)) continue;
                if (!freshInstall && IsUserOwnedPath(relative)) continue;
                var destination = ResolveSafeChild(profile.Path, relative);
                if (!File.Exists(destination)) continue;
                Backup(destination, backupRoot, relative, touched);
                File.Delete(destination);
            }

            if (patch.SchemaVersion >= 2)
            {
                var removed = ReconcileExactDirectories(profile.Path, patch, backupRoot, touched, cancellationToken);
                progress?.Report(new UpdateProgress(
                    97,
                    "Repairing file integrity",
                    removed == 0
                        ? "Managed mod inventory already clean"
                        : $"Quarantined {removed} unexpected managed file(s)"));
            }
            else
            {
                ReconcileModJars(profile.Path, patch, stagedPayload, backupRoot, touched);
            }

            // Existing profiles keep minecraftinstance.json byte-for-byte. Editing
            // CurseForge identity metadata can make its agent register a duplicate.
            if (freshInstall) RewriteProfileIdentity(profile.Path, installedProfileName, true, null);

            VerifyInstalledFiles(profile.Path, patch, progress, cancellationToken);

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
            root["guid"] = identity.Guid;
            root["installPath"] = AbsoluteDirectoryPath(profilePath);
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

    private static void ReconcileModJars(
        string profilePath,
        PatchManifest patch,
        string stagedPayload,
        string backupRoot,
        List<TouchedFile> touched)
    {
        var managedJars = patch.Files
            .Select(file => NormalizeRelativePath(file.Path))
            .Where(IsModJar)
            .ToArray();
        if (managedJars.Length == 0) return;

        var managedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var managedModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in managedJars)
        {
            managedPaths.Add(Path.GetFullPath(ResolveSafeChild(profilePath, relative)));
            foreach (var modId in ReadModIds(ResolveSafeChild(stagedPayload, relative)))
            {
                managedModIds.Add(modId);
            }
        }
        if (managedModIds.Count == 0) return;

        var modsDirectory = Path.Combine(profilePath, "mods");
        if (!Directory.Exists(modsDirectory)) return;
        foreach (var existing in Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly))
        {
            var fullPath = Path.GetFullPath(existing);
            if (managedPaths.Contains(fullPath)) continue;

            var overlap = ReadModIds(fullPath)
                .Where(managedModIds.Contains)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (overlap.Length == 0) continue;

            var relative = Path.GetRelativePath(profilePath, fullPath).Replace('\\', '/');
            Backup(fullPath, backupRoot, relative, touched);
            File.Delete(fullPath);
            AppLog.Info(
                $"Removed superseded mod {Path.GetFileName(fullPath)}; replacement provides: {string.Join(", ", overlap)}");
        }
    }

    private static int ReconcileExactDirectories(
        string profilePath,
        PatchManifest patch,
        string backupRoot,
        List<TouchedFile> touched,
        CancellationToken cancellationToken)
    {
        var expectedPaths = patch.Files
            .Select(file => Path.GetFullPath(ResolveSafeChild(profilePath, NormalizeRelativePath(file.Path))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;

        foreach (var exactDirectory in patch.ExactDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativeDirectory = NormalizeRelativePath(exactDirectory);
            var directory = ResolveSafeChild(profilePath, relativeDirectory);
            if (!Directory.Exists(directory)) continue;

            foreach (var existing in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(existing);
                if (expectedPaths.Contains(fullPath)) continue;

                var relative = Path.GetRelativePath(profilePath, fullPath).Replace('\\', '/');
                Backup(fullPath, backupRoot, relative, touched);
                File.Delete(fullPath);
                removed++;
                AppLog.Info($"Quarantined unexpected managed file: {relative}");
            }
        }

        return removed;
    }

    private static void VerifyInstalledFiles(
        string profilePath,
        PatchManifest patch,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < patch.Files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = patch.Files[index];
            var relative = NormalizeRelativePath(file.Path);
            if (IsProfileMetadata(relative) || IsUserOwnedPath(relative)) continue;

            var installed = ResolveSafeChild(profilePath, relative);
            if (!File.Exists(installed))
            {
                throw new InvalidDataException($"Installed file is missing after repair: {relative}");
            }
            var info = new FileInfo(installed);
            if (file.Size >= 0 && info.Length != file.Size)
            {
                throw new InvalidDataException($"Installed file size mismatch after repair: {relative}");
            }
            var hash = ComputeSha256(installed);
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException($"Installed file checksum mismatch after repair: {relative}");
            }

            if (index % 64 == 0 || index == patch.Files.Length - 1)
            {
                progress?.Report(new UpdateProgress(
                    98 + (index + 1) * 1.5d / Math.Max(1, patch.Files.Length),
                    "Validating installed client",
                    relative));
            }
        }

        var expectedPaths = patch.Files
            .Select(file => Path.GetFullPath(ResolveSafeChild(profilePath, NormalizeRelativePath(file.Path))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var exactDirectory in patch.ExactDirectories)
        {
            var directory = ResolveSafeChild(profilePath, NormalizeRelativePath(exactDirectory));
            if (!Directory.Exists(directory)) continue;
            var unexpected = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .FirstOrDefault(path => !expectedPaths.Contains(Path.GetFullPath(path)));
            if (unexpected is not null)
            {
                throw new InvalidDataException(
                    $"Unexpected managed file remains after repair: {Path.GetRelativePath(profilePath, unexpected)}");
            }
        }
    }

    private static void ValidatePatchInventory(PatchManifest patch)
    {
        var files = patch.Files.Select(file => NormalizeRelativePath(file.Path)).ToArray();
        var duplicateFile = files.GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateFile is not null)
        {
            throw new InvalidDataException($"Patch contains a duplicate file path: {duplicateFile}");
        }

        var deletes = patch.Delete.Select(NormalizeRelativePath).ToArray();
        var duplicateDelete = deletes.GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateDelete is not null)
        {
            throw new InvalidDataException($"Patch contains a duplicate delete path: {duplicateDelete}");
        }
        var fileSet = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflict = deletes.FirstOrDefault(fileSet.Contains);
        if (conflict is not null)
        {
            throw new InvalidDataException($"Patch both installs and deletes the same path: {conflict}");
        }

        var exactDirectories = patch.ExactDirectories.Select(NormalizeRelativePath).ToArray();
        if (patch.SchemaVersion == 1 && exactDirectories.Length != 0)
        {
            throw new InvalidDataException("Schema version 1 cannot declare exact managed directories.");
        }
        if (patch.SchemaVersion >= 2 &&
            !exactDirectories.Contains("mods", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Schema version 2 must declare mods as an exact managed directory.");
        }
        var duplicateDirectory = exactDirectories.GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateDirectory is not null)
        {
            throw new InvalidDataException($"Patch contains a duplicate exact directory: {duplicateDirectory}");
        }
        foreach (var directory in exactDirectories)
        {
            if (IsProfileMetadata(directory) || IsUserOwnedPath(directory))
            {
                throw new InvalidDataException($"Patch cannot strictly manage a user-owned path: {directory}");
            }
        }
    }

    private static bool IsModJar(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        return normalized.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) &&
               normalized.Count(character => character == '/') == 1 &&
               normalized.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlySet<string> ReadModIds(string jarPath)
    {
        var modIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var metadata = archive.Entries.FirstOrDefault(entry =>
                string.Equals(entry.FullName, "META-INF/neoforge.mods.toml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.FullName, "META-INF/mods.toml", StringComparison.OrdinalIgnoreCase));
            if (metadata is null) return modIds;

            using var reader = new StreamReader(metadata.Open(), Encoding.UTF8, true);
            var text = reader.ReadToEnd();
            foreach (Match section in Regex.Matches(
                         text,
                         @"(?ms)^\s*\[\[mods\]\]\s*(?<body>.*?)(?=^\s*\[|\z)",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var id = Regex.Match(
                    section.Groups["body"].Value,
                    @"(?m)^\s*modId\s*=\s*[""'](?<id>[a-z0-9_.-]+)[""']",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (id.Success) modIds.Add(id.Groups["id"].Value);
            }

            foreach (Match inlineList in Regex.Matches(
                         text,
                         @"(?ms)^\s*mods\s*=\s*\[(?<body>.*?)\]\s*$",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                foreach (Match id in Regex.Matches(
                             inlineList.Groups["body"].Value,
                             @"\bmodId\s*=\s*[""'](?<id>[a-z0-9_.-]+)[""']",
                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    modIds.Add(id.Groups["id"].Value);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Could not inspect mod metadata: {jarPath}", exception);
        }
        return modIds;
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

    private static bool IsProfileMetadata(string relative) =>
        string.Equals(relative, "minecraftinstance.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsUserOwnedPath(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        return string.Equals(normalized, "config/DistantHorizons.toml", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Distant_Horizons_server_data/", StringComparison.OrdinalIgnoreCase);
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

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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
