using System.Windows;
using MvCraftoriaUpdater.Services;

namespace MvCraftoriaUpdater;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsInstanceMutex;
    internal bool SelfUpdateCompleted { get; private set; }
    internal bool SelfUpdateSucceeded { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (SelfUpdateService.TryRunHelper(e.Args, out var helperExitCode))
        {
            Shutdown(helperExitCode);
            return;
        }

        var cleanupSession = SelfUpdateService.ReadCleanupSession(
            e.Args,
            out var helperProcessId,
            out var updateSucceeded);
        SelfUpdateCompleted = cleanupSession is not null;
        SelfUpdateSucceeded = updateSucceeded;
        instanceMutex = new Mutex(true, @"Local\MV.Craftoria.Updater.SingleInstance", out ownsInstanceMutex);
        if (!ownsInstanceMutex)
        {
            MessageBox.Show(
                "MV Craftoria Updater is already running.",
                "MV Craftoria Updater",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }
        WorkDirectoryCleaner.CleanupAbandonedSessions(cleanupSession);
        base.OnStartup(e);
        if (cleanupSession is not null)
        {
            SelfUpdateService.ScheduleCleanup(cleanupSession, helperProcessId);
        }
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(
                "MV Craftoria Updater encountered an unexpected error. No update files were applied.\n\n" +
                "Details were written to the updater log.",
                "MV Craftoria Updater",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (instanceMutex is not null)
        {
            if (ownsInstanceMutex) instanceMutex.ReleaseMutex();
            instanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
