using System.Windows;
using Microsoft.Win32;
using MvCraftoriaUpdater.Services;
using MvCraftoriaUpdater.ViewModels;

namespace MvCraftoriaUpdater;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;

    public MainWindow()
    {
        InitializeComponent();
        var configuration = ConfigurationService.Load();
        viewModel = new MainViewModel(configuration)
        {
            ConfirmInstall = (title, message, primaryText) =>
                UpdaterDialog.Confirm(this, title, message, primaryText),
            ShowMessage = (message, title) =>
                UpdaterDialog.ShowInformation(this, title, message)
        };
        viewModel.BrowseRequested += BrowseForProfile;
        viewModel.BrowseInstancesRequested += BrowseForInstances;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closed += (_, _) => viewModel.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await viewModel.InitializeAsync();
        if (Application.Current is not App { SelfUpdateCompleted: true } app) return;

        if (app.SelfUpdateSucceeded)
        {
            UpdaterDialog.ShowInformation(
                this,
                "Updater update complete",
                $"MV Craftoria Updater {VersionPolicy.RunningUpdaterVersion} is installed and ready.");
        }
        else
        {
            UpdaterDialog.ShowInformation(
                this,
                "Updater update failed",
                "The updater could not replace itself. The existing executable was reopened and temporary files are being removed. Open Logs for details.");
        }
    }

    private void BrowseForProfile(object? sender, EventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the MV Craftoria CurseForge profile",
            InitialDirectory = viewModel.SelectedProfile?.Path,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) viewModel.AddManualProfile(dialog.FolderName);
    }

    private void BrowseForInstances(object? sender, EventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the CurseForge Minecraft modding folder or Instances folder",
            Multiselect = false,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == true) viewModel.SetInstanceRoot(dialog.FolderName);
    }
}
