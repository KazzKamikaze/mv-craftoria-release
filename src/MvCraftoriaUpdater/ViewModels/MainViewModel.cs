using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using MvCraftoriaUpdater.Models;
using MvCraftoriaUpdater.Services;

namespace MvCraftoriaUpdater.ViewModels;

internal sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string NotInstalledVersion = "NOT_INSTALLED";
    private readonly UpdaterConfiguration configuration;
    private readonly CurseForgeLocator locator;
    private readonly GitHubReleaseClient releaseClient;
    private readonly UpdateEngine updateEngine;
    private CurseForgeProfile? selectedProfile;
    private VerifiedRelease? latestRelease;
    private string currentVersion = "Not detected";
    private string latestVersion = "Not checked";
    private string statusTitle = "Starting updater";
    private string statusDetail = "Locating your MV Craftoria profile";
    private string securityStatus = "SIGNED UPDATE CHANNEL";
    private double progress;
    private bool isBusy;
    private string profileStatus = "CURSEFORGE PROFILE DETECTED";
    private string primaryActionText = "Update Now";

    internal MainViewModel(UpdaterConfiguration configuration)
    {
        this.configuration = configuration;
        locator = new CurseForgeLocator();
        releaseClient = new GitHubReleaseClient(configuration);
        updateEngine = new UpdateEngine(configuration);
        CheckCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsBusy);
        UpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, CanInstall);
        BrowseCommand = new RelayCommand(() => BrowseRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        OpenReleaseCommand = new RelayCommand(OpenRelease, () => latestRelease is not null);
        OpenLogsCommand = new RelayCommand(OpenLogs);
    }

    internal event EventHandler? BrowseRequested;
    internal Func<string, bool>? ConfirmInstall { get; set; }
    internal Action<string, string>? ShowMessage { get; set; }

    public ObservableCollection<CurseForgeProfile> Profiles { get; } = [];
    public ObservableCollection<string> Changelog { get; } = [];

    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand OpenReleaseCommand { get; }
    public RelayCommand OpenLogsCommand { get; }

    public CurseForgeProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (!SetProperty(ref selectedProfile, value)) return;
            CurrentVersion = value?.Version ?? "Not detected";
            OnPropertyChanged(nameof(ProfilePath));
            RefreshCommandStates();
            EvaluateReleaseState();
        }
    }

    public string ProfilePath => SelectedProfile?.Path ?? "No compatible CurseForge profile selected";
    public string CurrentVersion { get => currentVersion; private set => SetProperty(ref currentVersion, value); }
    public string LatestVersion { get => latestVersion; private set => SetProperty(ref latestVersion, value); }
    public string StatusTitle { get => statusTitle; private set => SetProperty(ref statusTitle, value); }
    public string StatusDetail { get => statusDetail; private set => SetProperty(ref statusDetail, value); }
    public string SecurityStatus { get => securityStatus; private set => SetProperty(ref securityStatus, value); }
    public string ProfileStatus { get => profileStatus; private set => SetProperty(ref profileStatus, value); }
    public string PrimaryActionText { get => primaryActionText; private set => SetProperty(ref primaryActionText, value); }
    public double Progress { get => progress; private set => SetProperty(ref progress, value); }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value)) return;
            RefreshCommandStates();
        }
    }

    internal async Task InitializeAsync()
    {
        ReloadProfiles();
        if (Profiles.Count == 0)
        {
            var instanceRoot = locator.FindPreferredInstanceRoot();
            if (instanceRoot is not null)
            {
                SelectedProfile = new CurseForgeProfile(
                    configuration.ProductName,
                    Path.Combine(instanceRoot, configuration.ProductName),
                    NotInstalledVersion,
                    "1.21.1");
                CurrentVersion = "Not installed";
                ProfileStatus = "CURSEFORGE INSTALL LOCATION DETECTED";
                PrimaryActionText = "Install Client";
                StatusTitle = "MV Craftoria is ready to install";
                StatusDetail = "The latest signed client will be installed into CurseForge automatically.";
            }
            else
            {
                StatusTitle = "CurseForge was not found";
                StatusDetail = "Install CurseForge or use Choose Another to select its Instances folder.";
            }
        }

        if (IsRepositoryConfigured())
        {
            await CheckForUpdatesAsync();
        }
        else
        {
            SecurityStatus = "SOURCE SETUP REQUIRED";
            StatusTitle = Profiles.Count > 0 ? "Client profile detected" : StatusTitle;
            StatusDetail = "The updater is ready. Configure the GitHub release repository before publishing it.";
        }
    }

    internal bool AddManualProfile(string path)
    {
        var profile = locator.TryReadProfile(path, configuration.ProductName);
        if (profile is null)
        {
            ShowMessage?.Invoke(
                "That folder is not an MV Craftoria CurseForge profile. Select the folder containing minecraftinstance.json.",
                "Profile not recognized");
            return false;
        }

        var existing = Profiles.FirstOrDefault(item =>
            string.Equals(item.Path, profile.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is null) Profiles.Add(profile);
        SelectedProfile = existing ?? profile;
        return true;
    }

    public void Dispose() => releaseClient.Dispose();

    private void ReloadProfiles()
    {
        var previousPath = SelectedProfile?.Path;
        Profiles.Clear();
        foreach (var profile in locator.FindProfiles(configuration.ProductName)) Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Path, previousPath, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
        if (SelectedProfile is not null)
        {
            ProfileStatus = "CURSEFORGE PROFILE DETECTED";
            PrimaryActionText = "Update Now";
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (!IsRepositoryConfigured())
        {
            SecurityStatus = "SOURCE SETUP REQUIRED";
            StatusTitle = "Update source is not configured";
            StatusDetail = "Set the GitHub owner/repository in updater-config.json.";
            return;
        }

        IsBusy = true;
        Progress = 12;
        StatusTitle = "Checking for updates";
        StatusDetail = "Verifying the latest signed GitHub release";
        try
        {
            latestRelease = await releaseClient.GetLatestVerifiedReleaseAsync(CancellationToken.None);
            LatestVersion = latestRelease.Manifest.Version;
            Changelog.Clear();
            foreach (var item in latestRelease.Manifest.Changelog) Changelog.Add(item);
            SecurityStatus = "RELEASE SIGNATURE VERIFIED";
            Progress = 0;
            EvaluateReleaseState();
            AppLog.Info($"Verified release {LatestVersion} from {configuration.Repository}");
        }
        catch (Exception exception)
        {
            latestRelease = null;
            LatestVersion = "Unavailable";
            Changelog.Clear();
            SecurityStatus = "UPDATE CHECK FAILED";
            StatusTitle = "Could not verify the update channel";
            StatusDetail = FriendlyError(exception);
            Progress = 0;
            AppLog.Error("Update check failed", exception);
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (SelectedProfile is null || latestRelease is null) return;
        var freshInstall = VersionPolicy.IsSame(SelectedProfile.Version, NotInstalledVersion);
        var prompt = $"{(freshInstall ? "Install" : "Update")} MV Craftoria {latestRelease.Manifest.Version}?\n\n" +
                     "Minecraft must be closed. Your controls, maps, saves, screenshots, shader settings, and Distant Horizons data are preserved.";
        if (ConfirmInstall?.Invoke(prompt) != true) return;

        IsBusy = true;
        var progressReporter = new Progress<UpdateProgress>(value =>
        {
            Progress = value.Percentage;
            StatusTitle = value.Stage;
            StatusDetail = value.Detail;
        });
        try
        {
            var backup = await updateEngine.InstallAsync(
                SelectedProfile,
                latestRelease,
                releaseClient,
                progressReporter,
                CancellationToken.None);
            ReloadProfiles();
            StatusTitle = "MV Craftoria is up to date";
            StatusDetail = $"Installed {latestRelease.Manifest.Version}. Recovery backup: {backup}";
            Progress = 100;
            ShowMessage?.Invoke("The update was installed successfully. You can now launch MV Craftoria from CurseForge.", "Update complete");
        }
        catch (Exception exception)
        {
            StatusTitle = "Update was not installed";
            StatusDetail = FriendlyError(exception);
            Progress = 0;
            AppLog.Error("Update installation failed", exception);
            ShowMessage?.Invoke(StatusDetail + "\n\nNo unverified update was applied. See the updater log for details.", "Update failed");
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private void EvaluateReleaseState()
    {
        if (latestRelease is null || SelectedProfile is null) return;
        if (VersionPolicy.IsSame(SelectedProfile.Version, latestRelease.Manifest.Version))
        {
            StatusTitle = "MV Craftoria is up to date";
            StatusDetail = latestRelease.Manifest.Summary;
        }
        else if (latestRelease.Manifest.SupportedFrom.Contains(SelectedProfile.Version, StringComparer.OrdinalIgnoreCase))
        {
            StatusTitle = $"Update {latestRelease.Manifest.Version} is ready";
            StatusDetail = latestRelease.Manifest.Summary;
        }
        else
        {
            StatusTitle = "This client needs a repair package";
            StatusDetail = $"Version {SelectedProfile.Version} cannot update directly to {latestRelease.Manifest.Version}.";
        }
        RefreshCommandStates();
    }

    private bool CanInstall() =>
        !IsBusy &&
        SelectedProfile is not null &&
        latestRelease is not null &&
        !VersionPolicy.IsSame(SelectedProfile.Version, latestRelease.Manifest.Version) &&
        latestRelease.Manifest.SupportedFrom.Contains(SelectedProfile.Version, StringComparer.OrdinalIgnoreCase);

    private bool IsRepositoryConfigured() =>
        !string.IsNullOrWhiteSpace(configuration.Repository) &&
        !configuration.Repository.Contains("CONFIGURE", StringComparison.OrdinalIgnoreCase);

    private void RefreshCommandStates()
    {
        CheckCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        BrowseCommand.RaiseCanExecuteChanged();
        OpenReleaseCommand.RaiseCanExecuteChanged();
    }

    private void OpenRelease()
    {
        if (latestRelease is not null) OpenUri(latestRelease.ReleasePageUri.AbsoluteUri);
    }

    private static void OpenLogs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppLog.CurrentLogPath)!);
        OpenUri(File.Exists(AppLog.CurrentLogPath)
            ? AppLog.CurrentLogPath
            : Path.GetDirectoryName(AppLog.CurrentLogPath)!);
    }

    private static void OpenUri(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private static string FriendlyError(Exception exception) => exception switch
    {
        HttpRequestException => "GitHub could not be reached. Check your connection and try again.",
        CryptographicException => "The release failed signature or checksum verification and was rejected.",
        _ => exception.Message
    };
}
