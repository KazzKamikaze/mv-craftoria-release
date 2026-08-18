namespace MvCraftoriaUpdater.Services;

internal static class WorkDirectoryCleaner
{
    private const int DeleteAttempts = 6;

    internal static string WorkRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MV Craftoria Updater",
        "work");

    internal static void CleanupAbandonedSessions(string? excludedDirectory = null)
    {
        if (!Directory.Exists(WorkRoot)) return;

        foreach (var directory in Directory.EnumerateDirectories(WorkRoot))
        {
            if (excludedDirectory is not null &&
                string.Equals(Path.GetFullPath(directory), Path.GetFullPath(excludedDirectory), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            DeleteDirectory(directory, "abandoned updater download");
        }

        foreach (var file in Directory.EnumerateFiles(WorkRoot))
        {
            DeleteFile(file, "abandoned updater download");
        }
    }

    internal static bool DeleteDirectory(string path, string description)
    {
        if (!Directory.Exists(path)) return true;

        Exception? lastError = null;
        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            try
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                AppLog.Info($"Removed {description}: {path}");
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception;
                if (attempt < DeleteAttempts) Thread.Sleep(attempt * 200);
            }
        }

        AppLog.Error($"Could not remove {description}: {path}", lastError!);
        return false;
    }

    private static void DeleteFile(string path, string description)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            AppLog.Info($"Removed {description}: {path}");
        }
        catch (FileNotFoundException)
        {
            // Another cleanup pass already removed it.
        }
        catch (Exception exception)
        {
            AppLog.Error($"Could not remove {description}: {path}", exception);
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }
}
