using System.Reflection;

namespace MvCraftoriaUpdater.Services;

internal static class VersionPolicy
{
    private const string LegacyFinalSuffix = "-final";

    internal static string RunningUpdaterVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    internal static void EnsureUpdaterVersionSupported(string minimumVersion)
    {
        if (!IsRunningUpdaterSupported(minimumVersion))
        {
            throw new InvalidOperationException(
                $"This release requires MV Craftoria Updater {minimumVersion} or newer.");
        }
    }

    internal static bool IsRunningUpdaterSupported(string minimumVersion)
    {
        if (!TryParse(minimumVersion, out var minimum) || !TryParse(RunningUpdaterVersion, out var running))
        {
            throw new InvalidDataException("The release contains an invalid updater version requirement.");
        }
        return running >= minimum;
    }

    internal static bool IsNewerThanRunning(string candidateVersion)
    {
        if (!TryParse(candidateVersion, out var candidate) || !TryParse(RunningUpdaterVersion, out var running))
        {
            throw new InvalidDataException("The release contains an invalid updater version.");
        }
        return candidate > running;
    }

    internal static int Compare(string left, string right)
    {
        if (!TryParse(left, out var leftVersion) || !TryParse(right, out var rightVersion))
        {
            throw new InvalidDataException("A release contains an invalid version.");
        }
        return leftVersion.CompareTo(rightVersion);
    }

    internal static bool IsSame(string left, string right) =>
        string.Equals(NormalizeLegacyLabel(left), NormalizeLegacyLabel(right), StringComparison.OrdinalIgnoreCase);

    internal static string Display(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return NormalizeLegacyLabel(value);
    }

    internal static string DisplayProfileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return value.Replace(LegacyFinalSuffix, "", StringComparison.OrdinalIgnoreCase).Trim();
    }

    internal static string ProfileName(string productName, string version) =>
        $"{productName.Trim()} {Display(version)}";

    private static string NormalizeLegacyLabel(string value)
    {
        var normalized = value.Trim();
        return normalized.EndsWith(LegacyFinalSuffix, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^LegacyFinalSuffix.Length]
            : normalized;
    }

    private static bool TryParse(string value, out Version version)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var suffix = normalized.IndexOfAny(['-', '+']);
        if (suffix >= 0) normalized = normalized[..suffix];
        return Version.TryParse(normalized, out version!);
    }
}
