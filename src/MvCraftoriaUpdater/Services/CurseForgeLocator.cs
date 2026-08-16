using System.Text.Json;
using System.Text.Json.Nodes;
using MvCraftoriaUpdater.Models;

namespace MvCraftoriaUpdater.Services;

internal sealed class CurseForgeLocator
{
    internal IReadOnlyList<CurseForgeProfile> FindProfiles(string expectedName)
    {
        var roots = FindInstanceRoots();
        var profiles = new Dictionary<string, CurseForgeProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var profilePath in ReadRegisteredProfilePaths())
        {
            var profile = TryReadProfile(profilePath, expectedName);
            if (profile is not null) profiles[NormalizeDirectory(profile.Path)] = profile;
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var profile = TryReadProfile(directory, expectedName);
                if (profile is not null) profiles[NormalizeDirectory(profile.Path)] = profile;
            }
        }

        AppLog.Info($"CurseForge discovery found {profiles.Count} profile(s). Roots: {string.Join("; ", roots)}");
        return profiles.Values.OrderBy(profile => profile.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal string? FindPreferredInstanceRoot() =>
        ReadRegisteredProfilePaths()
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        ?? FindInstanceRoots().FirstOrDefault(path => Directory.Exists(path));

    internal CurseForgeProfile? TryReadProfile(string directory, string expectedName)
    {
        try
        {
            var fullPath = NormalizeDirectory(directory);
            var instancePath = Path.Combine(fullPath, "minecraftinstance.json");
            if (!File.Exists(instancePath)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(instancePath));
            var root = document.RootElement;
            var name = ReadString(root, "name");
            var stateProduct = ReadStateProduct(fullPath);
            if (!string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith(expectedName + " ", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(stateProduct, expectedName, StringComparison.OrdinalIgnoreCase)) return null;

            return new CurseForgeProfile(
                name,
                fullPath,
                ReadInstalledVersion(fullPath, root),
                ReadString(root, "gameVersion"));
        }
        catch (Exception exception)
        {
            AppLog.Error($"Failed to inspect CurseForge profile: {directory}", exception);
            return null;
        }
    }

    internal async Task<CurseForgeProfile> WaitForRegisteredProfileAsync(
        string profilePath,
        string expectedName,
        string? expectedGuid,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeDirectory(profilePath);
        var databasePaths = FindDatabasePaths();
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var databasePath in databasePaths)
            {
                try
                {
                    if (!File.Exists(databasePath)) continue;
                    using var document = JsonDocument.Parse(File.ReadAllText(databasePath));
                    if (ContainsRegisteredProfile(document.RootElement, normalizedPath, expectedName, expectedGuid))
                    {
                        return TryReadProfile(profilePath, expectedName)
                            ?? throw new InvalidDataException("CurseForge registered the client, but its profile metadata is invalid.");
                    }
                }
                catch (Exception exception) when (exception is IOException or JsonException)
                {
                    // CurseForge rewrites these databases atomically; retry while one is between states.
                }
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException("CurseForge did not register the new client in My Modpacks.");
    }

    internal static bool ContainsRegisteredProfile(
        JsonElement databaseRoot,
        string profilePath,
        string expectedName,
        string? expectedGuid = null)
    {
        if (databaseRoot.ValueKind != JsonValueKind.Array) return false;
        var normalizedPath = NormalizeDirectory(profilePath);
        foreach (var instance in databaseRoot.EnumerateArray())
        {
            var installPath = NormalizeDirectory(ReadString(instance, "installPath"));
            var name = ReadString(instance, "name");
            var guid = ReadString(instance, "guid");
            if (string.Equals(installPath, normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(expectedGuid) || string.Equals(guid, expectedGuid, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    internal static string? TryReadProfileGuid(string profilePath)
    {
        try
        {
            var metadataPath = Path.Combine(profilePath, "minecraftinstance.json");
            if (!File.Exists(metadataPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var guid = ReadString(document.RootElement, "guid");
            return string.IsNullOrWhiteSpace(guid) ? null : guid;
        }
        catch { return null; }
    }

    internal static IReadOnlyList<string> FindInstanceRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var storagePath = Path.Combine(roaming, "CurseForge", "storage.json");
        var savedRoot = UpdaterSettingsService.LoadInstanceRoot();
        if (!string.IsNullOrWhiteSpace(savedRoot)) roots.Add(savedRoot);
        try
        {
            if (File.Exists(storagePath))
            {
                using var outerDocument = JsonDocument.Parse(File.ReadAllText(storagePath));
                if (outerDocument.RootElement.TryGetProperty("minecraft-settings", out var settingsElement) &&
                    settingsElement.ValueKind == JsonValueKind.String)
                {
                    var settingsJson = settingsElement.GetString();
                    if (!string.IsNullOrWhiteSpace(settingsJson))
                    {
                        using var settingsDocument = JsonDocument.Parse(settingsJson);
                        var minecraftRoot = ReadString(settingsDocument.RootElement, "minecraftRoot");
                        if (!string.IsNullOrWhiteSpace(minecraftRoot))
                        {
                            roots.Add(Path.GetFullPath(Path.Combine(minecraftRoot, "Instances")));
                        }
                    }
                }
            }
        }

        catch (Exception exception)
        {
            AppLog.Error("CurseForge storage discovery failed", exception);
        }

        foreach (var profilePath in ReadRegisteredProfilePaths())
        {
            var root = Path.GetDirectoryName(profilePath);
            if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
        }

        roots.Add(Path.Combine(user, "curseforge", "minecraft", "Instances"));
        roots.Add(Path.Combine(documents, "Curse", "Minecraft", "Instances"));
        roots.Add(Path.Combine(user, "Twitch", "Minecraft", "Instances"));
        return roots.Select(Path.GetFullPath).ToArray();
    }

    internal static string? ResolveInstanceRootSelection(string selectedFolder)
    {
        if (string.IsNullOrWhiteSpace(selectedFolder) || !Directory.Exists(selectedFolder)) return null;
        var selected = NormalizeDirectory(selectedFolder);
        if (File.Exists(Path.Combine(selected, "minecraftinstance.json")))
        {
            return Path.GetDirectoryName(selected);
        }
        if (string.Equals(Path.GetFileName(selected), "Instances", StringComparison.OrdinalIgnoreCase)) return selected;

        foreach (var relative in new[] { "Instances", Path.Combine("minecraft", "Instances") })
        {
            var candidate = Path.Combine(selected, relative);
            if (Directory.Exists(candidate)) return NormalizeDirectory(candidate);
        }
        return null;
    }

    private static IReadOnlyList<string> ReadRegisteredProfilePaths()
        => ReadRegisteredProfilePathsFromDatabases(FindDatabasePaths());

    internal static IReadOnlyList<string> ReadRegisteredProfilePathsFromDatabases(IEnumerable<string> databasePaths)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var databasePath in databasePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(databasePath)) continue;
                using var document = JsonDocument.Parse(File.ReadAllText(databasePath));
                if (document.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var instance in document.RootElement.EnumerateArray())
                {
                    var installPath = ReadString(instance, "installPath");
                    if (!string.IsNullOrWhiteSpace(installPath)) paths.Add(Path.GetFullPath(installPath));
                }
            }
            catch (Exception exception)
            {
                AppLog.Error($"CurseForge registered-profile discovery failed for {databasePath}", exception);
            }
        }
        return paths.ToArray();
    }

    internal static IReadOnlyList<string> FindDatabasePaths()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(roaming, "CurseForge", "agent", "GameInstances", "MinecraftGameInstance.json"),
            Path.Combine(local, "CurseForge", "agent", "GameInstances", "MinecraftGameInstance.json"),
            Path.Combine(local, "Overwolf", "CurseForge", "agent", "GameInstances", "MinecraftGameInstance.json")
        };

        foreach (var root in new[] { Path.Combine(local, "Overwolf"), Path.Combine(roaming, "Overwolf") })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(
                             root,
                             "MinecraftGameInstance.json",
                             SearchOption.AllDirectories))
                {
                    paths.Add(Path.GetFullPath(path));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Error($"Could not inspect Overwolf profile databases under {root}", exception);
            }
        }

        return paths.ToArray();
    }

    private static string ReadInstalledVersion(string profilePath, JsonElement instanceRoot)
    {
        try
        {
            var statePath = Path.Combine(profilePath, ".mv-update", "state.json");
            if (File.Exists(statePath))
            {
                using var state = JsonDocument.Parse(File.ReadAllText(statePath));
                var version = ReadString(state.RootElement, "version");
                if (!string.IsNullOrWhiteSpace(version)) return version;
            }

            var manifestPath = Path.Combine(profilePath, "manifest.json");
            if (File.Exists(manifestPath))
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var version = ReadString(manifest.RootElement, "version");
                if (!string.IsNullOrWhiteSpace(version)) return version;
            }

            if (instanceRoot.TryGetProperty("manifest", out var embeddedManifest))
            {
                var version = ReadString(embeddedManifest, "version");
                if (!string.IsNullOrWhiteSpace(version)) return version;
            }
        }
        catch (Exception exception)
        {
            AppLog.Error($"Version discovery failed for {profilePath}", exception);
        }

        return "Unknown";
    }

    private static string ReadStateProduct(string profilePath)
    {
        try
        {
            var statePath = Path.Combine(profilePath, ".mv-update", "state.json");
            if (!File.Exists(statePath)) return "";
            using var state = JsonDocument.Parse(File.ReadAllText(statePath));
            return ReadString(state.RootElement, "product");
        }
        catch { return ""; }
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var exact) && exact.ValueKind == JsonValueKind.String)
        {
            return exact.GetString() ?? "";
        }
        if (element.ValueKind != JsonValueKind.Object) return "";
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString() ?? "";
            }
        }
        return "";
    }

    private static string NormalizeDirectory(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? ""
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
