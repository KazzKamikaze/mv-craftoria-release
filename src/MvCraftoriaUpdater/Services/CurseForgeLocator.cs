using System.Text.Json;
using MvCraftoriaUpdater.Models;

namespace MvCraftoriaUpdater.Services;

internal sealed class CurseForgeLocator
{
    internal IReadOnlyList<CurseForgeProfile> FindProfiles(string expectedName)
    {
        var roots = FindInstanceRoots();
        var profiles = new Dictionary<string, CurseForgeProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var profile = TryReadProfile(directory, expectedName);
                if (profile is not null) profiles[profile.Path] = profile;
            }
        }

        AppLog.Info($"CurseForge discovery found {profiles.Count} profile(s). Roots: {string.Join("; ", roots)}");
        return profiles.Values.OrderBy(profile => profile.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal string? FindPreferredInstanceRoot() =>
        FindInstanceRoots().FirstOrDefault(path => Directory.Exists(path));

    internal CurseForgeProfile? TryReadProfile(string directory, string expectedName)
    {
        try
        {
            var fullPath = Path.GetFullPath(directory);
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
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeDirectory(profilePath);
        var databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CurseForge",
            "agent",
            "GameInstances",
            "MinecraftGameInstance.json");
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(databasePath))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(databasePath));
                    if (ContainsRegisteredProfile(document.RootElement, normalizedPath, expectedName))
                    {
                        return TryReadProfile(profilePath, expectedName)
                            ?? throw new InvalidDataException("CurseForge registered the client, but its profile metadata is invalid.");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // CurseForge rewrites this database atomically; retry while it is between states.
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException("CurseForge did not register the new client in My Modpacks.");
    }

    internal static bool ContainsRegisteredProfile(JsonElement databaseRoot, string profilePath, string expectedName)
    {
        if (databaseRoot.ValueKind != JsonValueKind.Array) return false;
        var normalizedPath = NormalizeDirectory(profilePath);
        foreach (var instance in databaseRoot.EnumerateArray())
        {
            var installPath = NormalizeDirectory(ReadString(instance, "installPath"));
            var name = ReadString(instance, "name");
            if (string.Equals(installPath, normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal static IReadOnlyList<string> FindInstanceRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var storagePath = Path.Combine(roaming, "CurseForge", "storage.json");
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

        roots.Add(Path.Combine(user, "curseforge", "minecraft", "Instances"));
        roots.Add(Path.Combine(documents, "Curse", "Minecraft", "Instances"));
        roots.Add(Path.Combine(user, "Twitch", "Minecraft", "Instances"));
        return roots.Select(Path.GetFullPath).ToArray();
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

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string NormalizeDirectory(string path) =>
        string.IsNullOrWhiteSpace(path)
            ? ""
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
