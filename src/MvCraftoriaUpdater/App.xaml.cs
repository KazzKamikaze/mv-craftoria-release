using System.Windows;
using MvCraftoriaUpdater.Services;

namespace MvCraftoriaUpdater;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
}
