using System.Text;
using System.Text.Json;

namespace MvCraftoriaUpdater.Services;

internal static class UpdaterSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MV Craftoria Updater",
        "settings.json");

    internal static string? LoadCurseForgeExecutable()
    {
        try
        {
            var settings = LoadSettings();
            return CurseForgeProcessService.IsSupportedExecutable(settings?.CurseForgeExecutable)
                ? Path.GetFullPath(settings!.CurseForgeExecutable!)
                : null;
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not read updater settings", exception);
            return null;
        }
    }

    internal static void SaveCurseForgeExecutable(string executablePath)
    {
        if (!CurseForgeProcessService.IsSupportedExecutable(executablePath))
        {
            throw new InvalidDataException("Select the CurseForge.exe application.");
        }

        var settings = LoadSettings() ?? new UpdaterSettings();
        settings.CurseForgeExecutable = Path.GetFullPath(executablePath);
        SaveSettings(settings);
    }

    internal static string? LoadInstanceRoot()
    {
        try
        {
            var path = LoadSettings()?.InstanceRoot;
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
                : null;
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not read the saved CurseForge Instances folder", exception);
            return null;
        }
    }

    internal static void SaveInstanceRoot(string instanceRoot)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instanceRoot));
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("Select an existing CurseForge Instances folder.");
        var settings = LoadSettings() ?? new UpdaterSettings();
        settings.InstanceRoot = fullPath;
        SaveSettings(settings);
    }

    private static UpdaterSettings? LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return null;
        return JsonSerializer.Deserialize<UpdaterSettings>(File.ReadAllText(SettingsPath), JsonDefaults.Options);
    }

    private static void SaveSettings(UpdaterSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("The updater settings directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonDefaults.Options);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed class UpdaterSettings
    {
        public string? CurseForgeExecutable { get; set; }
        public string? InstanceRoot { get; set; }
    }
}
