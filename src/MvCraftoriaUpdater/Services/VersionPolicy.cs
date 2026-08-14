using System.Reflection;

namespace MvCraftoriaUpdater.Services;

internal static class VersionPolicy
{
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
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TryParse(string value, out Version version)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var suffix = normalized.IndexOfAny(['-', '+']);
        if (suffix >= 0) normalized = normalized[..suffix];
        return Version.TryParse(normalized, out version!);
    }
}
