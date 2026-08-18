using System.Diagnostics;
using Microsoft.Win32;

namespace MvCraftoriaUpdater.Services;

internal static class CurseForgeProcessService
{
    internal static async Task<CurseForgeRestartSession> PrepareForMaintenanceAsync(
        CancellationToken cancellationToken)
    {
        var processes = GetMaintenanceProcesses();
        var hostedWindows = GetHostedCurseForgeWindows();
        var launchTarget = FindLaunchTarget(processes);

        var wasRunning = processes.Length > 0 || hostedWindows.Length > 0;
        if (wasRunning)
        {
            foreach (var process in processes.Concat(hostedWindows))
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
            Dispose(hostedWindows);

            var gracefulDeadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < gracefulDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var activeProcesses = GetMaintenanceProcesses();
                var activeHostedWindows = GetHostedCurseForgeWindows();
                var stopped = activeProcesses.Length == 0 && activeHostedWindows.Length == 0;
                Dispose(activeProcesses);
                Dispose(activeHostedWindows);
                if (stopped) break;
                await Task.Delay(250, cancellationToken);
            }

            var remaining = GetMaintenanceProcesses();
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
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    AppLog.Error($"Could not terminate CurseForge process {process.ProcessName}", exception);
                }
            }
            Dispose(remaining);

            // Some Overwolf installations expose the CurseForge window through a
            // separate host process. Clean up any remaining hosted window after the
            // normal CurseForge and Overwolf maintenance processes have exited.
            var remainingHostedWindows = GetHostedCurseForgeWindows();
            foreach (var process in remainingHostedWindows)
            {
                try
                {
                    process.Kill(entireProcessTree: false);
                }
                catch (InvalidOperationException)
                {
                    // The hosted window exited between enumeration and termination.
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    AppLog.Error($"Could not terminate hosted CurseForge window {process.ProcessName}", exception);
                }
            }
            Dispose(remainingHostedWindows);

            var forcedDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < forcedDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var active = GetMaintenanceProcesses();
                var activeHostedWindows = GetHostedCurseForgeWindows();
                var stopped = active.All(process => !IsBlockingMaintenanceProcess(process)) &&
                              activeHostedWindows.Length == 0;
                Dispose(active);
                Dispose(activeHostedWindows);
                if (stopped) break;
                await Task.Delay(250, cancellationToken);
            }
            var stillRunning = GetMaintenanceProcesses();
            var failedToClose = stillRunning.Any(IsBlockingMaintenanceProcess);
            Dispose(stillRunning);
            if (failedToClose) throw new InvalidOperationException("CurseForge could not be closed for maintenance.");

            var blockingWindows = GetHostedCurseForgeWindows();
            var hostedWindowStillOpen = blockingWindows.Length > 0;
            Dispose(blockingWindows);
            if (hostedWindowStillOpen)
            {
                throw new InvalidOperationException(
                    "CurseForge is still open. Close its window manually and run the update again. No files were changed.");
            }
        }
        else
        {
            Dispose(processes);
            Dispose(hostedWindows);
        }

        return new CurseForgeRestartSession(launchTarget);
    }

    internal static bool TryLaunchCurseForge(CurseForgeRestartSession session)
    {
        try
        {
            if (!IsSupportedLaunchTarget(session.LaunchTarget)) return false;
            var startInfo = new ProcessStartInfo(session.LaunchTarget!)
            {
                UseShellExecute = true
            };
            if (!IsCurseForgeProtocol(session.LaunchTarget))
            {
                startInfo.WorkingDirectory = Path.GetDirectoryName(session.LaunchTarget) ?? "";
            }
            Process.Start(startInfo);
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("CurseForge launch failed", exception);
            return false;
        }
    }

    private static string? FindLaunchTarget(IEnumerable<Process> processes)
    {
        var candidates = new List<string?>
        {
            UpdaterSettingsService.LoadCurseForgeExecutable(),
            ReadProtocolExecutablePath(),
            ReadUninstallExecutablePath(),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "CurseForge Windows",
                "CurseForge.exe")
        };
        var executable = candidates.FirstOrDefault(IsSupportedExecutable);
        if (executable is not null) return executable;

        var shortcut = FindCurseForgeShortcut();
        if (shortcut is not null) return shortcut;
        if (IsCurseForgeProtocolRegistered()) return "curseforge://";

        // Last resort for unusual standalone installations. Curse.Agent.Host is a
        // background service and must never be launched as the desktop application.
        foreach (var process in processes)
        {
            try
            {
                if (string.Equals(process.ProcessName, "Curse.Agent.Host", StringComparison.OrdinalIgnoreCase)) continue;
                var path = process.MainModule?.FileName;
                if (IsSupportedExecutable(path)) return path;
            }
            catch
            {
                // The process may exit while its executable path is inspected.
            }
        }
        return null;
    }

    internal static bool IsSupportedLaunchTarget(string? path)
    {
        if (IsSupportedExecutable(path)) return true;
        if (IsCurseForgeProtocol(path)) return IsCurseForgeProtocolRegistered();
        return !string.IsNullOrWhiteSpace(path) &&
               File.Exists(path) &&
               string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase) &&
               Path.GetFileNameWithoutExtension(path).Contains("CurseForge", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurseForgeProtocol(string? target) =>
        string.Equals(target, "curseforge://", StringComparison.OrdinalIgnoreCase);

    private static bool IsCurseForgeProtocolRegistered()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"curseforge\shell\open\command");
            return !string.IsNullOrWhiteSpace(key?.GetValue(null) as string);
        }
        catch { return false; }
    }

    private static string? FindCurseForgeShortcut()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        var shortcuts = new List<string>();
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                shortcuts.AddRange(Directory.EnumerateFiles(root, "*CurseForge*.lnk", SearchOption.AllDirectories));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Error($"Could not inspect CurseForge shortcuts under {root}", exception);
            }
        }

        return shortcuts
            .Where(IsSupportedLaunchTarget)
            .OrderBy(path => string.Equals(Path.GetFileName(path), "CurseForge.lnk", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path.Length)
            .FirstOrDefault();
    }

    internal static string? ParseExecutableFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            return closingQuote > 1 ? command[1..closingQuote] : null;
        }
        var exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd >= 0 ? command[..(exeEnd + 4)].Trim() : null;
    }

    internal static bool IsSupportedExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (fileName.Contains("CurseForge", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var information = FileVersionInfo.GetVersionInfo(path);
            return (information.ProductName?.Contains("CurseForge", StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (information.FileDescription?.Contains("CurseForge", StringComparison.OrdinalIgnoreCase) ?? false);
        }
        catch { return false; }
    }

    private static string? ReadProtocolExecutablePath()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"curseforge\shell\open\command");
            return ParseExecutableFromCommand(key?.GetValue(null) as string);
        }
        catch { return null; }
    }

    private static string? ReadUninstallExecutablePath()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var view in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var root = hive.OpenSubKey(view);
                    if (root is null) continue;
                    foreach (var childName in root.GetSubKeyNames())
                    {
                        using var child = root.OpenSubKey(childName);
                        var displayName = child?.GetValue("DisplayName") as string;
                        if (displayName?.Contains("CurseForge", StringComparison.OrdinalIgnoreCase) != true) continue;
                        var icon = ParseExecutableFromCommand(child?.GetValue("DisplayIcon") as string);
                        if (!string.IsNullOrWhiteSpace(icon)) return icon;
                        var location = child?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(location)) return Path.Combine(location, "CurseForge.exe");
                    }
                }
                catch { }
            }
        }
        return null;
    }

    private static void Dispose(IEnumerable<Process> processes)
    {
        foreach (var process in processes) process.Dispose();
    }

    internal static bool IsMaintenanceProcessName(string processName) =>
        processName.Contains("CurseForge", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(processName, "Curse.Agent.Host", StringComparison.OrdinalIgnoreCase) ||
        processName.Contains("Overwolf", StringComparison.OrdinalIgnoreCase);

    internal static bool IsBlockingMaintenanceProcessName(string processName) =>
        IsMaintenanceProcessName(processName) &&
        !string.Equals(processName, "Curse.Agent.Host", StringComparison.OrdinalIgnoreCase);

    internal static bool IsHostedCurseForgeWindow(string processName, string windowTitle) =>
        processName.Contains("Overwolf", StringComparison.OrdinalIgnoreCase) &&
        windowTitle.Contains("CurseForge", StringComparison.OrdinalIgnoreCase);

    private static Process[] GetMaintenanceProcesses() =>
        Process.GetProcesses()
            .Where(IsMaintenanceProcess)
            .ToArray();

    private static Process[] GetHostedCurseForgeWindows() =>
        Process.GetProcesses()
            .Where(IsHostedCurseForgeWindow)
            .ToArray();

    private static bool IsMaintenanceProcess(Process process)
    {
        try { return IsMaintenanceProcessName(process.ProcessName); }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsBlockingMaintenanceProcess(Process process)
    {
        try { return IsBlockingMaintenanceProcessName(process.ProcessName); }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsHostedCurseForgeWindow(Process process)
    {
        try { return IsHostedCurseForgeWindow(process.ProcessName, process.MainWindowTitle); }
        catch (InvalidOperationException) { return false; }
    }
}

internal sealed record CurseForgeRestartSession(string? LaunchTarget);
