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
    private CurseForgeProfile? selectedProfile;
    private VerifiedRelease? selectedRelease;
    private VerifiedRelease? updaterRelease;
    private CancellationTokenSource? operationCancellation;
    private string currentVersion = "Not selected";
    private string selectedVersion = "Not checked";
    private string statusTitle = "Starting updater";
    private string statusDetail = "Locating MV Craftoria clients";
    private string securityStatus = "SIGNED UPDATE CHANNEL";
    private string profileStatus = "CLIENT PROFILE";
    private double progress;
    private bool isBusy;
    private bool updaterUpdateAvailable;
    private string updaterNotice = "UPDATER CURRENT";

    internal MainViewModel(UpdaterConfiguration configuration)
    {
        this.configuration = configuration;
        releaseClient = new GitHubReleaseClient(configuration);
        updateEngine = new UpdateEngine(configuration);
        CheckCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsBusy);
        UpdateCommand = new AsyncRelayCommand(UpdateSelectedClientAsync, CanUpdateSelected);
        InstallNewCommand = new AsyncRelayCommand(InstallAsNewClientAsync, CanInstallNew);
        UpdateUpdaterCommand = new AsyncRelayCommand(UpdateUpdaterAsync, CanUpdateUpdater);
        CancelCommand = new RelayCommand(CancelOperation, () => CanCancel);
        BrowseCommand = new RelayCommand(() => BrowseRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        BrowseCurseForgeCommand = new RelayCommand(() => BrowseInstancesRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        OpenReleaseCommand = new RelayCommand(OpenRelease, () => SelectedRelease is not null);
        OpenLogsCommand = new RelayCommand(OpenLogs);
    }

    internal event EventHandler? BrowseRequested;
    internal event EventHandler? BrowseInstancesRequested;
    internal Func<string, string, string, bool>? ConfirmInstall { get; set; }
    internal Action<string, string>? ShowMessage { get; set; }

    public ObservableCollection<CurseForgeProfile> Profiles { get; } = [];
    public ObservableCollection<VerifiedRelease> Releases { get; } = [];
    public ObservableCollection<string> Changelog { get; } = [];
    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public AsyncRelayCommand InstallNewCommand { get; }
    public AsyncRelayCommand UpdateUpdaterCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand BrowseCurseForgeCommand { get; }
    public RelayCommand OpenReleaseCommand { get; }
    public RelayCommand OpenLogsCommand { get; }

    public CurseForgeProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (!SetProperty(ref selectedProfile, value)) return;
            CurrentVersion = value is null ? "Not selected" : VersionPolicy.Display(value.Version);
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
            SelectedVersion = value is null ? "Not selected" : VersionPolicy.Display(value.Manifest.Version);
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
    public string RunningUpdaterVersion => VersionPolicy.RunningUpdaterVersion;
    public string UpdaterNotice { get => updaterNotice; private set => SetProperty(ref updaterNotice, value); }
    public bool UpdaterUpdateAvailable
    {
        get => updaterUpdateAvailable;
        private set => SetProperty(ref updaterUpdateAvailable, value);
    }
    public double Progress { get => progress; private set => SetProperty(ref progress, value); }
    public bool CanCancel => IsBusy && operationCancellation is not null;
    public string UpdateActionText =>
        SelectedProfile is not null && SelectedRelease is not null &&
        VersionPolicy.IsSame(SelectedProfile.Version, SelectedRelease.Manifest.Version)
            ? "Repair Selected Client"
            : "Update Selected Client";

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value)) return;
            OnPropertyChanged(nameof(CanCancel));
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

    internal bool SetInstanceRoot(string path)
    {
        try
        {
            var root = CurseForgeLocator.ResolveInstanceRootSelection(path)
                ?? throw new DirectoryNotFoundException(
                    "Select the CurseForge Minecraft modding folder, its Instances folder, or an installed profile folder.");
            UpdaterSettingsService.SaveInstanceRoot(root);
            ReloadProfiles();
            ShowMessage?.Invoke(
                $"CurseForge Minecraft instances were located successfully.\n\n{root}",
                "Instances folder located");
            RefreshCommandStates();
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("Manual CurseForge Instances selection failed", exception);
            ShowMessage?.Invoke(FriendlyError(exception), "Minecraft folder not recognized");
            return false;
        }
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
            updaterRelease = FindNewestUpdaterRelease(verified);
            UpdaterUpdateAvailable = updaterRelease is not null;
            UpdaterNotice = updaterRelease is null
                ? $"UPDATER {VersionPolicy.Display(VersionPolicy.RunningUpdaterVersion)}"
                : $"UPDATER {VersionPolicy.Display(updaterRelease.Manifest.UpdaterVersion)} AVAILABLE";
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
            updaterRelease = null;
            UpdaterUpdateAvailable = false;
            UpdaterNotice = "UPDATER CHECK FAILED";
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
        var retainedName = SelectedProfile.Name;
        var reinstall = VersionPolicy.IsSame(SelectedProfile.Version, SelectedRelease.Manifest.Version);
        var prompt = reinstall
            ? $"{VersionPolicy.DisplayProfileName(SelectedProfile.Name)} is already up to date on " +
              $"{VersionPolicy.Display(SelectedRelease.Manifest.Version)}.\n\n" +
              "Do you want to repair this client against the signed release? Every managed file will be checksum-verified and reapplied. " +
              "Extra mod JARs, including versions left by CurseForge's Update All, will be quarantined in the rollback backup. " +
              "Personal settings, worlds, screenshots, map data, and the existing CurseForge profile identity will remain untouched. " +
              "CurseForge and Overwolf must close during the repair and will remain closed when it is finished."
            : $"{VersionPolicy.DisplayProfileName(SelectedProfile.Name)} will be updated from " +
              $"{VersionPolicy.Display(SelectedProfile.Version)} to {VersionPolicy.Display(SelectedRelease.Manifest.Version)} " +
              "in its existing profile.\n\n" +
              "The complete managed mod inventory will be checksum-verified, and extra mod JARs will be quarantined in the rollback backup. " +
              "Personal settings, worlds, screenshots, map data, profile name, and profile identity will remain untouched. " +
              "CurseForge and Overwolf must close during the update and will remain closed when it is finished.";
        var title = reinstall ? "Reinstall current version?" : "Confirm client update";
        var action = reinstall ? "Repair Client" : "Update Client";
        if (ConfirmInstall?.Invoke(title, prompt, action) != true) return;
        await InstallWithCurseForgeRestartAsync(
            SelectedProfile,
            SelectedRelease,
            retainedName,
            false,
            reinstall);
    }

    private async Task UpdateUpdaterAsync()
    {
        if (updaterRelease is null) return;
        var version = VersionPolicy.Display(updaterRelease.Manifest.UpdaterVersion);
        var prompt = $"MV Craftoria Updater {version} is available.\n\n" +
                     "The replacement executable is covered by the signed release manifest and will be checked for its exact size and SHA-256 checksum before anything changes. " +
                     "The updater will close, replace itself, reopen automatically, and remove its temporary files.";
        if (ConfirmInstall?.Invoke("Update MV Craftoria Updater?", prompt, "Update Updater") != true) return;

        operationCancellation = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancel));
        var cancellationToken = operationCancellation.Token;
        var session = Path.Combine(WorkDirectoryCleaner.WorkRoot, "self-update-" + Guid.NewGuid().ToString("N"));
        var downloadedUpdater = Path.Combine(session, "MV-Craftoria-Updater.verified.exe");
        Directory.CreateDirectory(session);
        IsBusy = true;
        try
        {
            StatusTitle = "Updating the updater";
            StatusDetail = $"Downloading and verifying updater {version}";
            Progress = 5;
            var progressReporter = new Progress<UpdateProgress>(value =>
            {
                Progress = value.Percentage;
                StatusTitle = value.Stage;
                StatusDetail = value.Detail;
            });
            await releaseClient.DownloadUpdaterAsync(
                updaterRelease,
                downloadedUpdater,
                progressReporter,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            StatusTitle = "Installing updater";
            StatusDetail = "The application will reopen automatically";
            Progress = 100;
            SelfUpdateService.BeginReplacement(downloadedUpdater, session);
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            WorkDirectoryCleaner.DeleteDirectory(session, "cancelled updater self-update");
            StatusTitle = "Updater update cancelled";
            StatusDetail = "The downloaded replacement was removed.";
            Progress = 0;
        }
        catch (Exception exception)
        {
            WorkDirectoryCleaner.DeleteDirectory(session, "failed updater self-update");
            StatusTitle = "Updater update failed";
            StatusDetail = FriendlyError(exception);
            Progress = 0;
            AppLog.Error("Updater self-update failed", exception);
            ShowMessage?.Invoke(
                StatusDetail + "\n\nThe current updater was not changed and temporary files were removed.",
                "Updater update failed");
        }
        finally
        {
            operationCancellation?.Dispose();
            operationCancellation = null;
            IsBusy = false;
            OnPropertyChanged(nameof(CanCancel));
            RefreshCommandStates();
        }
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
        var instanceRoot = locator.FindPreferredInstanceRoot()!;
        var target = new CurseForgeProfile(
            profileName,
            Path.Combine(instanceRoot, profileName),
            NotInstalledVersion,
            "1.21.1");
        var prompt = "This does NOT update your selected client. It creates a completely separate CurseForge profile.\n\n" +
                     $"A new CurseForge client named '{profileName}' will be installed with version " +
                     $"{VersionPolicy.Display(SelectedRelease.Manifest.Version)}.\n\n" +
                     "Your existing clients will not be changed. CurseForge and Overwolf must close during installation and CurseForge will " +
                     "reopen automatically when the new client is ready.";
        if (ConfirmInstall?.Invoke("Create a separate client?", prompt, "Create Separate Client") != true) return;
        await InstallWithCurseForgeRestartAsync(target, SelectedRelease, profileName, true, false);
    }

    private async Task InstallWithCurseForgeRestartAsync(
        CurseForgeProfile target,
        VerifiedRelease release,
        string installedName,
        bool newClient,
        bool reinstall)
    {
        var installed = false;
        var operationStage = "preparing CurseForge";
        CurseForgeRestartSession? maintenanceSession = null;
        operationCancellation = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancel));
        var cancellationToken = operationCancellation.Token;
        IsBusy = true;
        try
        {
            AppLog.Info($"Starting {(newClient ? "separate client installation" : "in-place client update")}: {target.Path}");
            StatusTitle = newClient ? "Installing MV Craftoria" : "Preparing CurseForge";
            StatusDetail = newClient
                ? "Preparing your new client"
                : "CurseForge and Overwolf are closing and will be unavailable until the operation finishes";
            maintenanceSession = await CurseForgeProcessService.PrepareForMaintenanceAsync(cancellationToken);
            operationStage = newClient ? "installing the new client" : "updating the selected client";
            installed = await InstallAsync(target, release, installedName, newClient, cancellationToken);
            if (!installed)
            {
                if (newClient)
                {
                    WorkDirectoryCleaner.DeleteDirectory(target.Path, "incomplete new client");
                }
                return;
            }

            var curseForgeOpenedForRegistration = newClient &&
                CurseForgeProcessService.TryLaunchCurseForge(maintenanceSession);
            if (newClient && !curseForgeOpenedForRegistration)
            {
                throw new FileNotFoundException(
                    "CurseForge could not be reopened. Start CurseForge once from Overwolf or its Start-menu shortcut, close it, and retry.");
            }

            if (newClient)
            {
                operationStage = "registering the new client in CurseForge";
                StatusTitle = "Finishing installation";
                StatusDetail = "Registering the client with CurseForge";
                var registered = await locator.WaitForRegisteredProfileAsync(
                    target.Path,
                    installedName,
                    CurseForgeLocator.TryReadProfileGuid(target.Path),
                    TimeSpan.FromMinutes(2),
                    cancellationToken);
                ReloadProfiles(registered.Path);
                Progress = 100;
                StatusTitle = "MV Craftoria is ready";
                StatusDetail = $"{registered.Name} is available in CurseForge My Modpacks.";
                ShowMessage?.Invoke(
                    $"{VersionPolicy.DisplayProfileName(registered.Name)} was installed successfully.\n\n" +
                    "CurseForge has reopened and the new client is ready in My Modpacks.",
                    "Installation complete");
            }
            else
            {
                StatusTitle = reinstall ? "Repair complete" : "Update complete";
                ReloadProfiles(target.Path);
                StatusDetail = $"{(reinstall ? "Reinstalled" : "Installed")} {VersionPolicy.Display(release.Manifest.Version)}. Open CurseForge to play.";
                ShowMessage?.Invoke(
                    $"{VersionPolicy.DisplayProfileName(target.Name)} was " +
                    $"{(reinstall ? "repaired successfully on" : "updated successfully to")} " +
                    $"{VersionPolicy.Display(release.Manifest.Version)}.\n\n" +
                    "All managed files and mod versions passed integrity verification. The original CurseForge profile was retained. " +
                    "Open CurseForge manually when you are ready to play.",
                    reinstall ? "Repair complete" : "Update complete");
            }
        }
        catch (OperationCanceledException)
        {
            if (newClient)
            {
                WorkDirectoryCleaner.DeleteDirectory(target.Path, "cancelled new client");
            }
            StatusTitle = "Installation cancelled";
            StatusDetail = "Downloaded and incomplete files were removed.";
            Progress = 0;
            AppLog.Info($"Cancelled {(newClient ? "new client installation" : "client update")}: {target.Path}");
        }
        catch (Exception exception)
        {
            if (newClient)
            {
                WorkDirectoryCleaner.DeleteDirectory(target.Path, "incomplete new client");
            }
            StatusTitle = newClient ? "Client installation failed" : "Update was not installed";
            StatusDetail = FailureDetail(operationStage, exception);
            Progress = 0;
            AppLog.Error($"Failed while {operationStage}: {target.Path}", exception);
            ShowMessage?.Invoke(
                StatusDetail + "\n\nTemporary downloads and incomplete client files were removed. " +
                "Use Open Logs if this error needs to be reported.",
                newClient ? "Installation failed" : "Update failed");
        }
        finally
        {
            operationCancellation.Dispose();
            operationCancellation = null;
            IsBusy = false;
            OnPropertyChanged(nameof(CanCancel));
            RefreshCommandStates();
        }
    }

    private async Task<bool> InstallAsync(
        CurseForgeProfile target,
        VerifiedRelease release,
        string installedName,
        bool newClient,
        CancellationToken cancellationToken)
    {
        var progressReporter = new Progress<UpdateProgress>(value =>
        {
            Progress = value.Percentage;
            if (newClient)
            {
                StatusTitle = "Installing MV Craftoria";
                StatusDetail = $"{Math.Clamp((int)Math.Round(value.Percentage), 0, 100)}% complete";
            }
            else
            {
                StatusTitle = value.Stage;
                StatusDetail = $"CurseForge and Overwolf are temporarily unavailable. {value.Detail}";
            }
        });
        try
        {
            var backup = await updateEngine.InstallAsync(
                target,
                release,
                releaseClient,
                progressReporter,
                cancellationToken,
                installedName);
            ReloadProfiles(target.Path);
            StatusTitle = newClient ? "New client created" : "Selected client updated";
            StatusDetail = newClient
                ? $"{installedName} was installed. Registering it with CurseForge."
                : $"Installed {VersionPolicy.Display(release.Manifest.Version)} in the selected profile. Recovery backup: {backup}";
            Progress = 100;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
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
        var displayVersion = VersionPolicy.Display(version);
        var safeVersion = string.Concat(displayVersion.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        var baseName = VersionPolicy.ProfileName(configuration.ProductName, safeVersion);
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
        OnPropertyChanged(nameof(UpdateActionText));
        if (SelectedRelease is null)
        {
            StatusTitle = "No release selected";
            StatusDetail = "Check GitHub for available signed versions.";
            RefreshCommandStates();
            return;
        }
        if (!VersionPolicy.IsRunningUpdaterSupported(SelectedRelease.Manifest.MinimumUpdaterVersion))
        {
            StatusTitle = "Updater update required";
            StatusDetail = $"Install MV Craftoria Updater {SelectedRelease.Manifest.MinimumUpdaterVersion} or newer before installing this client release.";
            RefreshCommandStates();
            return;
        }
        if (SelectedProfile is null)
        {
            StatusTitle = $"Version {VersionPolicy.Display(SelectedRelease.Manifest.Version)} is ready";
            StatusDetail = "Install it as a new CurseForge client.";
        }
        else if (VersionPolicy.IsSame(SelectedProfile.Version, SelectedRelease.Manifest.Version))
        {
            StatusTitle = "Client is up to date";
            StatusDetail = "Reinstall the selected release to verify and repair managed modpack files.";
        }
        else if (SelectedRelease.Manifest.SupportedFrom.Contains(SelectedProfile.Version, StringComparer.OrdinalIgnoreCase))
        {
            StatusTitle = $"{VersionPolicy.DisplayProfileName(SelectedProfile.Name)} can install {VersionPolicy.Display(SelectedRelease.Manifest.Version)}";
            StatusDetail = SelectedRelease.Manifest.Summary;
        }
        else
        {
            StatusTitle = "Direct update is not supported";
            StatusDetail = $"{VersionPolicy.Display(SelectedProfile.Version)} cannot update directly to " +
                           $"{VersionPolicy.Display(SelectedRelease.Manifest.Version)}. Install it as a new client instead.";
        }
        RefreshCommandStates();
    }

    private bool CanUpdateSelected() =>
        !IsBusy && SelectedProfile is not null && SelectedRelease is not null &&
        VersionPolicy.IsRunningUpdaterSupported(SelectedRelease.Manifest.MinimumUpdaterVersion) &&
        (VersionPolicy.IsSame(SelectedProfile.Version, SelectedRelease.Manifest.Version) ||
         SelectedRelease.Manifest.SupportedFrom.Contains(SelectedProfile.Version, StringComparer.OrdinalIgnoreCase));

    private bool CanInstallNew() =>
        !IsBusy && SelectedRelease is not null &&
        VersionPolicy.IsRunningUpdaterSupported(SelectedRelease.Manifest.MinimumUpdaterVersion) &&
        SelectedRelease.Manifest.SupportedFrom.Contains(NotInstalledVersion, StringComparer.OrdinalIgnoreCase) &&
        locator.FindPreferredInstanceRoot() is not null;

    private void RefreshCommandStates()
    {
        CheckCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        InstallNewCommand.RaiseCanExecuteChanged();
        UpdateUpdaterCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        BrowseCommand.RaiseCanExecuteChanged();
        BrowseCurseForgeCommand.RaiseCanExecuteChanged();
        OpenReleaseCommand.RaiseCanExecuteChanged();
    }

    private bool CanUpdateUpdater() => !IsBusy && updaterRelease is not null;

    private static VerifiedRelease? FindNewestUpdaterRelease(IEnumerable<VerifiedRelease> releases)
    {
        VerifiedRelease? newest = null;
        foreach (var release in releases.Where(item =>
                     item.Manifest.UpdaterPackage is not null &&
                     item.UpdaterPackageUri is not null &&
                     !string.IsNullOrWhiteSpace(item.Manifest.UpdaterVersion) &&
                     VersionPolicy.IsNewerThanRunning(item.Manifest.UpdaterVersion)))
        {
            if (newest is null ||
                VersionPolicy.Compare(release.Manifest.UpdaterVersion, newest.Manifest.UpdaterVersion) > 0)
            {
                newest = release;
            }
        }
        return newest;
    }

    private void CancelOperation()
    {
        if (operationCancellation is null || operationCancellation.IsCancellationRequested) return;
        StatusTitle = "Cancelling installation";
        StatusDetail = "Removing downloaded and incomplete files";
        operationCancellation.Cancel();
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.RaiseCanExecuteChanged();
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
        _ => string.IsNullOrWhiteSpace(exception.Message)
            ? $"{exception.GetType().Name} occurred without an error message."
            : exception.Message
    };

    private static string FailureDetail(string stage, Exception exception) =>
        $"The updater failed while {stage}.\n\n{FriendlyError(exception)}\n\n" +
        $"Diagnostic type: {exception.GetType().Name}";
}
