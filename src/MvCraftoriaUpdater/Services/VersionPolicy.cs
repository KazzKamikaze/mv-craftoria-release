using System.Reflection;

namespace MvCraftoriaUpdater.Services;

internal static class VersionPolicy
{
    private const string LegacyFinalSuffix = "-final";

    internal static void EnsureUpdaterVersionSupported(string minimumVersion)
    {
        var runningText = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        if (!TryParse(minimumVersion, out var minimum) || !TryParse(runningText, out var running))
        {
            throw new InvalidDataException("The release contains an invalid updater version requirement.");
        }
        if (running < minimum)
        {
            throw new InvalidOperationException(
                $"This release requires MV Craftoria Updater {minimumVersion} or newer.");
        }
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
