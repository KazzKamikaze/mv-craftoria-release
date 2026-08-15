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
    private readonly CurseForgeLocator locator = new();
    private readonly GitHubReleaseClient releaseClient;
    private readonly UpdateEngine updateEngine;
    private readonly CurseForgeImportService importService;
    private CurseForgeProfile? selectedProfile;
    private VerifiedRelease? selectedRelease;
    private string currentVersion = "Not selected";
    private string selectedVersion = "Not checked";
    private string statusTitle = "Starting updater";
    private string statusDetail = "Locating MV Craftoria clients";
    private string securityStatus = "SIGNED UPDATE CHANNEL";
    private string profileStatus = "CLIENT PROFILE";
    private double progress;
    private bool isBusy;

    internal MainViewModel(UpdaterConfiguration configuration)
    {
        this.configuration = configuration;
        releaseClient = new GitHubReleaseClient(configuration);
        updateEngine = new UpdateEngine(configuration);
        importService = new CurseForgeImportService(configuration, locator);
        CheckCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsBusy);
        UpdateCommand = new AsyncRelayCommand(UpdateSelectedClientAsync, CanUpdateSelected);
        InstallNewCommand = new AsyncRelayCommand(InstallAsNewClientAsync, CanInstallNew);
        BrowseCommand = new RelayCommand(() => BrowseRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        OpenReleaseCommand = new RelayCommand(OpenRelease, () => SelectedRelease is not null);
        OpenLogsCommand = new RelayCommand(OpenLogs);
    }

    internal event EventHandler? BrowseRequested;
    internal Func<string, bool>? ConfirmInstall { get; set; }
    internal Action<string, string>? ShowMessage { get; set; }

    public ObservableCollection<CurseForgeProfile> Profiles { get; } = [];
    public ObservableCollection<VerifiedRelease> Releases { get; } = [];
    public ObservableCollection<string> Changelog { get; } = [];
    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public AsyncRelayCommand InstallNewCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand OpenReleaseCommand { get; }
    public RelayCommand OpenLogsCommand { get; }

    public CurseForgeProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (!SetProperty(ref selectedProfile, value)) return;
            CurrentVersion = value?.Version ?? "Not selected";
            ProfileStatus = value is null ? "NO CLIENT SELECTED" : "SELECTED CURSEFORGE CLIENT";
            OnPropertyChanged(nameof(ProfilePath));
            EvaluateSelection();
        }
    }

    public VerifiedRelease? SelectedRelease
    {
        get => selectedRelease;
        set
        {
            if (!SetProperty(ref selectedRelease, value)) return;
            SelectedVersion = value?.Manifest.Version ?? "Not selected";
            Changelog.Clear();
            if (value is not null)
            {
                foreach (var item in value.Manifest.Changelog) Changelog.Add(item);
            }
            EvaluateSelection();
        }
    }

    public string ProfilePath => SelectedProfile?.Path ?? "Choose a client or install the selected release as a new profile";
    public string CurrentVersion { get => currentVersion; private set => SetProperty(ref currentVersion, value); }
    public string SelectedVersion { get => selectedVersion; private set => SetProperty(ref selectedVersion, value); }
    public string StatusTitle { get => statusTitle; private set => SetProperty(ref statusTitle, value); }
    public string StatusDetail { get => statusDetail; private set => SetProperty(ref statusDetail, value); }
    public string SecurityStatus { get => securityStatus; private set => SetProperty(ref securityStatus, value); }
    public string ProfileStatus { get => profileStatus; private set => SetProperty(ref profileStatus, value); }
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
            ProfileStatus = locator.FindPreferredInstanceRoot() is null
                ? "CURSEFORGE NOT DETECTED"
                : "CURSEFORGE READY FOR INSTALLATION";
        }
        await CheckForUpdatesAsync();
    }

    internal bool AddManualProfile(string path)
    {
        var profile = locator.TryReadProfile(path, configuration.ProductName);
        if (profile is null)
        {
            ShowMessage?.Invoke(
                "That folder is not an MV Craftoria client. Select a profile folder containing minecraftinstance.json.",
                "Client not recognized");
            return false;
        }
        var existing = Profiles.FirstOrDefault(item =>
            string.Equals(item.Path, profile.Path, StringComparison.OrdinalIgnoreCase));
        if (existing is null) Profiles.Add(profile);
        SelectedProfile = existing ?? profile;
        return true;
    }

    public void Dispose() => releaseClient.Dispose();

    private void ReloadProfiles(string? preferredPath = null)
    {
        preferredPath ??= SelectedProfile?.Path;
        Profiles.Clear();
        foreach (var profile in locator.FindProfiles(configuration.ProductName)) Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Path, preferredPath, StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
    }

    private async Task CheckForUpdatesAsync()
    {
        IsBusy = true;
        Progress = 12;
        StatusTitle = "Loading available versions";
        StatusDetail = "Downloading and verifying signed GitHub release manifests";
        try
        {
            var previousVersion = SelectedRelease?.Manifest.Version;
            var verified = await releaseClient.GetVerifiedReleasesAsync(CancellationToken.None);
            Releases.Clear();
            foreach (var release in verified) Releases.Add(release);
            SelectedRelease = Releases.FirstOrDefault(item =>
                VersionPolicy.IsSame(item.Manifest.Version, previousVersion ?? "")) ?? Releases.FirstOrDefault();
            SecurityStatus = $"{Releases.Count} SIGNED VERSION{(Releases.Count == 1 ? "" : "S")} VERIFIED";
            Progress = 0;
            EvaluateSelection();
            AppLog.Info($"Verified {Releases.Count} release(s) from {configuration.Repository}");
        }
        catch (Exception exception)
        {
            Releases.Clear();
            SelectedRelease = null;
            SecurityStatus = "UPDATE CHECK FAILED";
            StatusTitle = "Could not load modpack versions";
            StatusDetail = FriendlyError(exception);
            Progress = 0;
            AppLog.Error("Release list check failed", exception);
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private async Task UpdateSelectedClientAsync()
    {
        if (SelectedProfile is null || SelectedRelease is null) return;
        var prompt = $"Update '{SelectedProfile.Name}' from {SelectedProfile.Version} to {SelectedRelease.Manifest.Version}?\n\n" +
                     "Only the selected client will be modified. Personal settings and world data remain untouched.\n\n" +
                     "IMPORTANT: CurseForge will close before the update and remain unavailable while files are being installed. " +
                     "It will reopen automatically when the update finishes.";
        if (ConfirmInstall?.Invoke(prompt) != true) return;
        await InstallWithCurseForgeRestartAsync(
            SelectedProfile,
            SelectedRelease,
            SelectedProfile.Name,
            false);
    }

    private async Task InstallAsNewClientAsync()
    {
        if (SelectedRelease is null) return;
        var profileName = CreateNewProfileName(SelectedRelease.Manifest.Version);
        if (profileName is null)
        {
            ShowMessage?.Invoke("CurseForge's Minecraft Instances directory could not be located.", "CurseForge not found");
            return;
        }
        var prompt = $"Import a separate CurseForge client named '{profileName}'?\n\n" +
                     $"Version {SelectedRelease.Manifest.Version} will be installed through CurseForge's official Import Profile workflow.\n\n" +
                     "Your currently selected client will not be changed.\n\n" +
                     "IMPORTANT: CurseForge will restart and remain busy while it downloads and registers the client. " +
                     "The updater will select All Files only after verifying the signed package.";
        if (ConfirmInstall?.Invoke(prompt) != true) return;

        IsBusy = true;
        try
        {
            var reporter = new Progress<UpdateProgress>(value =>
            {
                Progress = value.Percentage;
                StatusTitle = value.Stage;
                StatusDetail = value.Detail;
            });
            var imported = await importService.ImportAsync(
                SelectedRelease,
                profileName,
                releaseClient,
                reporter,
                CancellationToken.None);
            ReloadProfiles(imported.Path);
            Progress = 100;
            StatusTitle = "New client installed";
            StatusDetail = $"{imported.Name} is registered in CurseForge My Modpacks.";
            ShowMessage?.Invoke(
                $"'{imported.Name}' was imported successfully and now appears in CurseForge My Modpacks.",
                "Client installed");
        }
        catch (Exception exception)
        {
            StatusTitle = "New client was not installed";
            StatusDetail = FriendlyError(exception);
            Progress = 0;
            AppLog.Error("CurseForge profile import failed", exception);
            ShowMessage?.Invoke(
                StatusDetail + "\n\nCurseForge did not report a completed profile import. See the updater log for details.",
                "Import failed");
        }
        finally
        {
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private async Task InstallWithCurseForgeRestartAsync(
        CurseForgeProfile target,
        VerifiedRelease release,
        string installedName,
        bool newClient)
    {
        CurseForgeRestartSession? restartSession = null;
        var installed = false;
        var curseForgeReopened = false;
        IsBusy = true;
        try
        {
            StatusTitle = "Preparing CurseForge";
            StatusDetail = "CurseForge is closing and will be unavailable until the operation finishes";
            restartSession = await CurseForgeProcessService.PrepareForMaintenanceAsync(CancellationToken.None);
            installed = await InstallAsync(target, release, installedName, newClient);
        }
        catch (Exception exception)
        {
            StatusTitle = "CurseForge could not be prepared";
            StatusDetail = FriendlyError(exception);
            AppLog.Error("CurseForge maintenance preparation failed", exception);
            ShowMessage?.Invoke(StatusDetail, "CurseForge restart required");
        }
        finally
        {
            if (restartSession is not null)
            {
                try
                {
                    CurseForgeProcessService.Launch(restartSession);
                    curseForgeReopened = true;
                }
                catch (Exception exception)
                {
                    AppLog.Error("CurseForge relaunch failed", exception);
                }
            }
            IsBusy = false;
            RefreshCommandStates();
        }

        if (!installed) return;
        if (curseForgeReopened)
        {
            StatusDetail = newClient
                ? $"{installedName} is ready. CurseForge has reopened and will register the client."
                : $"Installed {release.Manifest.Version}. CurseForge has reopened.";
            ShowMessage?.Invoke(
                newClient
                    ? $"'{installedName}' was installed as a separate CurseForge client. CurseForge has reopened."
                    : $"'{installedName}' was updated successfully. CurseForge has reopened.",
                newClient ? "Client created" : "Update complete");
        }
        else
        {
            StatusDetail = "The files were installed, but CurseForge could not be reopened automatically.";
            ShowMessage?.Invoke(
                "The files were installed successfully, but CurseForge could not be reopened automatically. Open CurseForge manually to finish refreshing the client list.",
                "Open CurseForge");
        }
    }

    private async Task<bool> InstallAsync(
        CurseForgeProfile target,
        VerifiedRelease release,
        string installedName,
        bool newClient)
    {
        var progressReporter = new Progress<UpdateProgress>(value =>
        {
            Progress = value.Percentage;
            StatusTitle = value.Stage;
            StatusDetail = $"CurseForge is temporarily unavailable. {value.Detail}";
        });
        try
        {
            var backup = await updateEngine.InstallAsync(
                target,
                release,
                releaseClient,
                progressReporter,
                CancellationToken.None,
                installedName);
            ReloadProfiles(target.Path);
            StatusTitle = newClient ? "New client created" : "Selected client updated";
            StatusDetail = newClient
                ? $"{installedName} was installed. Reopening CurseForge."
                : $"Installed {release.Manifest.Version}. Reopening CurseForge. Recovery backup: {backup}";
            Progress = 100;
            return true;
        }
        catch (Exception exception)
        {
            StatusTitle = newClient ? "New client was not created" : "Update was not installed";
            StatusDetail = FriendlyError(exception);
            Progress = 0;
            AppLog.Error(newClient ? "New client installation failed" : "Client update failed", exception);
            ShowMessage?.Invoke(StatusDetail + "\n\nNo unverified files were applied. See the updater log for details.", "Installation failed");
            return false;
        }
    }

    private string? CreateNewProfileName(string version)
    {
        var root = locator.FindPreferredInstanceRoot();
        if (root is null) return null;
        var displayVersion = version.EndsWith("-final", StringComparison.OrdinalIgnoreCase)
            ? version[..^"-final".Length]
            : version;
        var safeVersion = string.Concat(displayVersion.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        var baseName = $"MV Craftoria {safeVersion}";
        var name = baseName;
        var suffix = 2;
        while (Directory.Exists(Path.Combine(root, name)) ||
               Profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} ({suffix++})";
        }
        return name;
    }

    private void EvaluateSelection()
    {
        if (SelectedRelease is null)
        {
            StatusTitle = "No release selected";
            StatusDetail = "Check GitHub for available signed versions.";
            RefreshCommandStates();
            return;
        }
        if (SelectedProfile is null)
        {
            StatusTitle = $"Version {SelectedRelease.Manifest.Version} is ready";
            StatusDetail = "Install it as a new CurseForge client.";
        }
        else if (VersionPolicy.IsSame(SelectedProfile.Version, SelectedRelease.Manifest.Version))
        {
            StatusTitle = "Selected client already has this version";
            StatusDetail = "Choose another version or install this release as a separate client.";
        }
        else if (SelectedRelease.Manifest.SupportedFrom.Contains(SelectedProfile.Version, StringComparer.OrdinalIgnoreCase))
        {
            StatusTitle = $"{SelectedProfile.Name} can install {SelectedRelease.Manifest.Version}";
            StatusDetail = SelectedRelease.Manifest.Summary;
        }
        else
        {
            StatusTitle = "Direct update is not supported";
            StatusDetail = $"{SelectedProfile.Version} cannot update directly to {SelectedRelease.Manifest.Version}. Install it as a new client instead.";
        }
        RefreshCommandStates();
    }

    private bool CanUpdateSelected() =>
        !IsBusy && SelectedProfile is not null && SelectedRelease is not null &&
        !VersionPolicy.IsSame(SelectedProfile.Version, SelectedRelease.Manifest.Version) &&
        SelectedRelease.Manifest.SupportedFrom.Contains(SelectedProfile.Version, StringComparer.OrdinalIgnoreCase);

    private bool CanInstallNew() =>
        !IsBusy && SelectedRelease is not null &&
        SelectedRelease.Manifest.ImportPackage is not null &&
        SelectedRelease.ImportPackageUri is not null &&
        SelectedRelease.Manifest.SupportedFrom.Contains(NotInstalledVersion, StringComparer.OrdinalIgnoreCase) &&
        locator.FindPreferredInstanceRoot() is not null;

    private void RefreshCommandStates()
    {
        CheckCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        InstallNewCommand.RaiseCanExecuteChanged();
        BrowseCommand.RaiseCanExecuteChanged();
        OpenReleaseCommand.RaiseCanExecuteChanged();
    }

    private void OpenRelease()
    {
        if (SelectedRelease is not null) OpenUri(SelectedRelease.ReleasePageUri.AbsoluteUri);
    }

    private static void OpenLogs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppLog.CurrentLogPath)!);
        OpenUri(File.Exists(AppLog.CurrentLogPath) ? AppLog.CurrentLogPath : Path.GetDirectoryName(AppLog.CurrentLogPath)!);
    }

    private static void OpenUri(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private static string FriendlyError(Exception exception) => exception switch
    {
        HttpRequestException => "GitHub could not be reached. Check your connection and try again.",
        CryptographicException => "A release failed signature or checksum verification and was rejected.",
        _ => exception.Message
    };
}
