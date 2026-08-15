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
    private CancellationTokenSource? operationCancellation;
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
        CheckCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsBusy);
        UpdateCommand = new AsyncRelayCommand(UpdateSelectedClientAsync, CanUpdateSelected);
        InstallNewCommand = new AsyncRelayCommand(InstallAsNewClientAsync, CanInstallNew);
        CancelCommand = new RelayCommand(CancelOperation, () => CanCancel);
        BrowseCommand = new RelayCommand(() => BrowseRequested?.Invoke(this, EventArgs.Empty), () => !IsBusy);
        OpenReleaseCommand = new RelayCommand(OpenRelease, () => SelectedRelease is not null);
        OpenLogsCommand = new RelayCommand(OpenLogs);
    }

    internal event EventHandler? BrowseRequested;
    internal Func<string, string, string, bool>? ConfirmInstall { get; set; }
    internal Action<string, string>? ShowMessage { get; set; }

    public ObservableCollection<CurseForgeProfile> Profiles { get; } = [];
    public ObservableCollection<VerifiedRelease> Releases { get; } = [];
    public ObservableCollection<string> Changelog { get; } = [];
    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand UpdateCommand { get; }
    public AsyncRelayCommand InstallNewCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseCommand { get; }
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
    public double Progress { get => progress; private set => SetProperty(ref progress, value); }
    public bool CanCancel => IsBusy && operationCancellation is not null;

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
        var updatedName = VersionPolicy.ProfileName(configuration.ProductName, SelectedRelease.Manifest.Version);
        var prompt = $"{VersionPolicy.DisplayProfileName(SelectedProfile.Name)} will be updated from " +
                     $"{VersionPolicy.Display(SelectedProfile.Version)} to {VersionPolicy.Display(SelectedRelease.Manifest.Version)} " +
                     $"and renamed to '{updatedName}'.\n\n" +
                     "Personal settings, worlds, screenshots, and map data will remain untouched. CurseForge must close " +
                     "during the update and will reopen automatically when it is finished.";
        if (ConfirmInstall?.Invoke("Confirm client update", prompt, "Update Client") != true) return;
        await InstallWithCurseForgeRestartAsync(
            SelectedProfile,
            SelectedRelease,
            updatedName,
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
        var instanceRoot = locator.FindPreferredInstanceRoot()!;
        var target = new CurseForgeProfile(
            profileName,
            Path.Combine(instanceRoot, profileName),
            NotInstalledVersion,
            "1.21.1");
        var prompt = $"A new CurseForge client named '{profileName}' will be installed with version " +
                     $"{VersionPolicy.Display(SelectedRelease.Manifest.Version)}.\n\n" +
                     "Your existing clients will not be changed. CurseForge must close during installation and will " +
                     "reopen automatically when the new client is ready.";
        if (ConfirmInstall?.Invoke("Confirm new client installation", prompt, "Install Client") != true) return;
        await InstallWithCurseForgeRestartAsync(target, SelectedRelease, profileName, true);
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
        operationCancellation = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancel));
        var cancellationToken = operationCancellation.Token;
        IsBusy = true;
        try
        {
            StatusTitle = newClient ? "Installing MV Craftoria" : "Preparing CurseForge";
            StatusDetail = newClient
                ? "Preparing your new client"
                : "CurseForge is closing and will be unavailable until the operation finishes";
            restartSession = await CurseForgeProcessService.PrepareForMaintenanceAsync(cancellationToken);
            installed = await InstallAsync(target, release, installedName, newClient, cancellationToken);
            if (!installed)
            {
                if (newClient)
                {
                    WorkDirectoryCleaner.DeleteDirectory(target.Path, "incomplete new client");
                }
                return;
            }

            CurseForgeProcessService.Launch(restartSession, startMinimized: newClient);
            curseForgeReopened = true;

            if (newClient)
            {
                StatusTitle = "Finishing installation";
                StatusDetail = "Registering the client with CurseForge";
                var registered = await locator.WaitForRegisteredProfileAsync(
                    target.Path,
                    installedName,
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
                StatusTitle = "Update complete";
                StatusDetail = $"Installed {VersionPolicy.Display(release.Manifest.Version)}. CurseForge has reopened.";
                ShowMessage?.Invoke(
                    $"{VersionPolicy.DisplayProfileName(installedName)} was updated successfully to " +
                    $"{VersionPolicy.Display(release.Manifest.Version)}.\n\nCurseForge has reopened and the client is ready to play.",
                    "Update complete");
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
            StatusTitle = newClient ? "Client installation failed" : "CurseForge could not be prepared";
            StatusDetail = FriendlyError(exception);
            Progress = 0;
            AppLog.Error(newClient ? "Background client installation failed" : "CurseForge maintenance preparation failed", exception);
            ShowMessage?.Invoke(
                StatusDetail + "\n\nTemporary downloads and incomplete client files were removed.",
                newClient ? "Installation failed" : "CurseForge restart required");
        }
        finally
        {
            if (restartSession is not null && !curseForgeReopened)
            {
                try
                {
                    CurseForgeProcessService.Launch(restartSession, startMinimized: newClient);
                    curseForgeReopened = true;
                }
                catch (Exception exception)
                {
                    AppLog.Error("CurseForge relaunch failed", exception);
                }
            }
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
                StatusDetail = $"CurseForge is temporarily unavailable. {value.Detail}";
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
                ? $"{installedName} was installed. Reopening CurseForge."
                : $"Installed {VersionPolicy.Display(release.Manifest.Version)}. Reopening CurseForge. Recovery backup: {backup}";
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
        if (SelectedRelease is null)
        {
            StatusTitle = "No release selected";
            StatusDetail = "Check GitHub for available signed versions.";
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
            StatusTitle = "Selected client already has this version";
            StatusDetail = "Choose another version or install this release as a separate client.";
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
        !VersionPolicy.IsSame(SelectedProfile.Version, SelectedRelease.Manifest.Version) &&
        SelectedRelease.Manifest.SupportedFrom.Contains(SelectedProfile.Version, StringComparer.OrdinalIgnoreCase);

    private bool CanInstallNew() =>
        !IsBusy && SelectedRelease is not null &&
        SelectedRelease.Manifest.SupportedFrom.Contains(NotInstalledVersion, StringComparer.OrdinalIgnoreCase) &&
        locator.FindPreferredInstanceRoot() is not null;

    private void RefreshCommandStates()
    {
        CheckCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        InstallNewCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        BrowseCommand.RaiseCanExecuteChanged();
        OpenReleaseCommand.RaiseCanExecuteChanged();
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
        _ => exception.Message
    };
}
