using System.Text.Json;
using MvCraftoriaUpdater.Models;

namespace MvCraftoriaUpdater.Services;

internal static class ConfigurationService
{
    internal static UpdaterConfiguration Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "updater-config.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The updater configuration is missing.", path);
        }

        return JsonSerializer.Deserialize<UpdaterConfiguration>(
            File.ReadAllText(path),
            JsonDefaults.Options) ?? throw new InvalidDataException("Updater configuration is invalid.");
    }
}
