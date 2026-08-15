using System.Diagnostics;

namespace MvCraftoriaUpdater.Services;

internal static class CurseForgeProcessService
{
    private const string ProcessName = "CurseForge";

    internal static async Task<CurseForgeRestartSession> PrepareProfileRegistrationAsync(
        CancellationToken cancellationToken)
    {
        var processes = Process.GetProcessesByName(ProcessName);
        var executablePath = FindExecutablePath(processes);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            Dispose(processes);
            throw new FileNotFoundException("The CurseForge application could not be located.");
        }

        var wasRunning = processes.Length > 0;
        if (wasRunning)
        {
            var closeRequested = false;
            foreach (var process in processes)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow()) closeRequested = true;
                }
                catch
                {
                    // A helper process may exit while the process list is being inspected.
                }
            }
            Dispose(processes);
            if (!closeRequested)
            {
                throw new InvalidOperationException(
                    "CurseForge is running in the background. Exit it completely, then try again.");
            }

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = Process.GetProcessesByName(ProcessName);
                var stopped = remaining.Length == 0;
                Dispose(remaining);
                if (stopped) break;
                await Task.Delay(250, cancellationToken);
            }

            var stillRunning = Process.GetProcessesByName(ProcessName);
            var failedToClose = stillRunning.Length > 0;
            Dispose(stillRunning);
            if (failedToClose)
            {
                throw new InvalidOperationException(
                    "CurseForge did not close cleanly. Exit it completely, then try again.");
            }
        }
        else
        {
            Dispose(processes);
        }

        return new CurseForgeRestartSession(executablePath, wasRunning);
    }

    internal static void Launch(CurseForgeRestartSession session)
    {
        Process.Start(new ProcessStartInfo(session.ExecutablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(session.ExecutablePath) ?? ""
        });
    }

    private static string? FindExecutablePath(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
            catch
            {
                // Fall through to the standard installation path.
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "CurseForge Windows",
            "CurseForge.exe");
    }

    private static void Dispose(IEnumerable<Process> processes)
    {
        foreach (var process in processes) process.Dispose();
    }
}

internal sealed record CurseForgeRestartSession(string ExecutablePath, bool WasRunning);
