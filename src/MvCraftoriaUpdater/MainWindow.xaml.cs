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
            ConfirmInstall = message => MessageBox.Show(
                this, message, "Install MV Craftoria update", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes,
            ShowMessage = (message, title) => MessageBox.Show(
                this, message, title, MessageBoxButton.OK, MessageBoxImage.Information)
        };
        viewModel.BrowseRequested += BrowseForProfile;
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        Closed += (_, _) => viewModel.Dispose();
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
}
