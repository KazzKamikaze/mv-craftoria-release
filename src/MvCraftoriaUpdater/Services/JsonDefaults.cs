using System.Text.Json;

namespace MvCraftoriaUpdater.Services;

internal static class JsonDefaults
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
