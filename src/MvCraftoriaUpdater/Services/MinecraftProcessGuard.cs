using System.Diagnostics;

namespace MvCraftoriaUpdater.Services;

internal static class MinecraftProcessGuard
{
    internal static bool IsMinecraftRunning()
    {
        foreach (var name in new[] { "java", "javaw" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainWindowTitle.Contains("Minecraft", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch
                    {
                        // Inaccessible helper JVMs are not treated as the game client.
                    }
                }
            }
        }
        return false;
    }
}
