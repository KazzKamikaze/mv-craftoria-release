using System.Diagnostics;
using System.Security.Cryptography;

namespace MvCraftoriaUpdater.Services;

internal static class SelfUpdateService
{
    private const string ApplyArgument = "--apply-self-update";
    private const string CleanupArgument = "--cleanup-self-update";

    internal static bool TryRunHelper(string[] arguments, out int exitCode)
    {
        exitCode = 0;
        if (arguments.Length == 0 || !string.Equals(arguments[0], ApplyArgument, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (arguments.Length != 4 || !int.TryParse(arguments[2], out var oldProcessId))
            {
                throw new InvalidDataException("The self-update helper arguments are invalid.");
            }
            ApplyReplacement(arguments[1], oldProcessId, arguments[3]);
        }
        catch (Exception exception)
        {
            AppLog.Error("Updater self-replacement failed", exception);
            TryRestartTarget(arguments.ElementAtOrDefault(1), arguments.ElementAtOrDefault(3), "failed");
            exitCode = 1;
        }
        return true;
    }

    internal static string? ReadCleanupSession(
        string[] arguments,
        out int helperProcessId,
        out bool updateSucceeded)
    {
        helperProcessId = 0;
        updateSucceeded = false;
        if (arguments.Length != 4 || !string.Equals(arguments[0], CleanupArgument, StringComparison.Ordinal) ||
            !int.TryParse(arguments[2], out helperProcessId))
        {
            return null;
        }
        updateSucceeded = string.Equals(arguments[3], "success", StringComparison.OrdinalIgnoreCase);
        return Path.GetFullPath(arguments[1]);
    }

    internal static void BeginReplacement(string verifiedUpdaterPath, string sessionDirectory)
    {
        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The running updater path could not be determined.");
        EnsureTargetIsWritable(currentExecutable);

        var startInfo = new ProcessStartInfo(verifiedUpdaterPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(verifiedUpdaterPath)!
        };
        startInfo.ArgumentList.Add(ApplyArgument);
        startInfo.ArgumentList.Add(currentExecutable);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add(Path.GetFullPath(sessionDirectory));
        _ = Process.Start(startInfo) ??
            throw new InvalidOperationException("The updater replacement helper could not be started.");
    }

    internal static void ScheduleCleanup(string sessionDirectory, int helperProcessId)
    {
        _ = Task.Run(() =>
        {
            WaitForProcessExit(helperProcessId, TimeSpan.FromSeconds(30));
            WorkDirectoryCleaner.DeleteDirectory(sessionDirectory, "completed updater self-update");
        });
    }

    private static void ApplyReplacement(string targetExecutable, int oldProcessId, string sessionDirectory)
    {
        var helperExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The replacement helper path could not be determined.");
        targetExecutable = Path.GetFullPath(targetExecutable);
        sessionDirectory = Path.GetFullPath(sessionDirectory);
        EnsureSessionPath(sessionDirectory);
        WaitForProcessExit(oldProcessId, TimeSpan.FromSeconds(45));

        ReplaceExecutable(helperExecutable, targetExecutable);
        RestartTarget(targetExecutable, sessionDirectory, "success");
    }

    internal static void ReplaceExecutable(string verifiedExecutable, string targetExecutable)
    {
        verifiedExecutable = Path.GetFullPath(verifiedExecutable);
        targetExecutable = Path.GetFullPath(targetExecutable);
        var targetDirectory = Path.GetDirectoryName(targetExecutable)
            ?? throw new InvalidDataException("The updater target folder is invalid.");
        Directory.CreateDirectory(targetDirectory);
        var stagedTarget = Path.Combine(targetDirectory, $".{Path.GetFileName(targetExecutable)}.{Guid.NewGuid():N}.new");
        try
        {
            File.Copy(verifiedExecutable, stagedTarget, overwrite: true);
            VerifySameFile(verifiedExecutable, stagedTarget);
            File.Move(stagedTarget, targetExecutable, overwrite: true);
            VerifySameFile(verifiedExecutable, targetExecutable);
        }
        finally
        {
            if (File.Exists(stagedTarget)) File.Delete(stagedTarget);
        }
    }

    private static void TryRestartTarget(string? targetExecutable, string? sessionDirectory, string result)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(targetExecutable) || string.IsNullOrWhiteSpace(sessionDirectory)) return;
            RestartTarget(Path.GetFullPath(targetExecutable), Path.GetFullPath(sessionDirectory), result);
        }
        catch (Exception exception)
        {
            AppLog.Error("The updater could not be reopened after a self-update failure", exception);
        }
    }

    private static void RestartTarget(string targetExecutable, string sessionDirectory, string result)
    {
        var startInfo = new ProcessStartInfo(targetExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(targetExecutable)!
        };
        startInfo.ArgumentList.Add(CleanupArgument);
        startInfo.ArgumentList.Add(sessionDirectory);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add(result);
        _ = Process.Start(startInfo) ??
            throw new InvalidOperationException("The updater application could not be reopened.");
    }

    private static void EnsureTargetIsWritable(string targetExecutable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetExecutable))
            ?? throw new InvalidDataException("The updater target folder is invalid.");
        var probe = Path.Combine(directory, $".mv-updater-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(
                "The updater cannot replace itself in its current folder. Move it to Desktop or another writable folder and try again.",
                exception);
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
        }
    }

    private static void EnsureSessionPath(string sessionDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkDirectoryCleaner.WorkRoot));
        if (!sessionDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The updater session path is outside the trusted work directory.");
        }
    }

    private static void WaitForProcessExit(int processId, TimeSpan timeout)
    {
        if (processId <= 0 || processId == Environment.ProcessId) return;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                throw new TimeoutException("The previous updater process did not close in time.");
            }
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }
    }

    private static void VerifySameFile(string source, string destination)
    {
        using var sourceStream = File.OpenRead(source);
        using var destinationStream = File.OpenRead(destination);
        var sourceHash = SHA256.HashData(sourceStream);
        var destinationHash = SHA256.HashData(destinationStream);
        if (!sourceHash.SequenceEqual(destinationHash))
        {
            throw new CryptographicException("The staged updater executable failed verification.");
        }
    }
}
