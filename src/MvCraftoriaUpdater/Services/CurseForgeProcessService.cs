using System.Diagnostics;

namespace MvCraftoriaUpdater.Services;

internal static class CurseForgeProcessService
{
    private const string ProcessName = "CurseForge";

    internal static async Task<CurseForgeRestartSession> PrepareForMaintenanceAsync(
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
            foreach (var process in processes)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow();
                }
                catch
                {
                    // A helper process may exit while the process list is being inspected.
                }
            }
            Dispose(processes);

            var gracefulDeadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < gracefulDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var activeProcesses = Process.GetProcessesByName(ProcessName);
                var stopped = activeProcesses.Length == 0;
                Dispose(activeProcesses);
                if (stopped) break;
                await Task.Delay(250, cancellationToken);
            }

            var remaining = Process.GetProcessesByName(ProcessName);
            foreach (var process in remaining)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and termination.
                }
            }
            Dispose(remaining);

            var forcedDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < forcedDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var active = Process.GetProcessesByName(ProcessName);
                var stopped = active.Length == 0;
                Dispose(active);
                if (stopped) break;
                await Task.Delay(250, cancellationToken);
            }
            var stillRunning = Process.GetProcessesByName(ProcessName);
            var failedToClose = stillRunning.Length > 0;
            Dispose(stillRunning);
            if (failedToClose) throw new InvalidOperationException("CurseForge could not be closed for maintenance.");
        }
        else
        {
            Dispose(processes);
        }

        return new CurseForgeRestartSession(executablePath);
    }

    internal static void Launch(
        CurseForgeRestartSession session,
        bool startMinimized = false)
    {
        var arguments = new List<string>();
        if (startMinimized) arguments.Add("--minimized");
        Process.Start(new ProcessStartInfo(session.ExecutablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(session.ExecutablePath) ?? "",
            Arguments = string.Join(' ', arguments)
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

internal sealed record CurseForgeRestartSession(string ExecutablePath);
