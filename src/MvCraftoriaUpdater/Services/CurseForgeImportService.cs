using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using MvCraftoriaUpdater.Models;

namespace MvCraftoriaUpdater.Services;

internal sealed class CurseForgeImportService
{
    private const int FileNameControlId = 1148;
    private const int OpenButtonControlId = 1;
    private const uint ButtonClick = 0x00F5;
    private readonly UpdaterConfiguration configuration;
    private readonly CurseForgeLocator locator;

    internal CurseForgeImportService(UpdaterConfiguration configuration, CurseForgeLocator locator)
    {
        this.configuration = configuration;
        this.locator = locator;
    }

    internal async Task<CurseForgeProfile> ImportAsync(
        VerifiedRelease release,
        string profileName,
        GitHubReleaseClient releaseClient,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var importPackage = release.Manifest.ImportPackage
            ?? throw new InvalidOperationException("This version cannot be installed as a new CurseForge client.");
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MV Craftoria Updater");
        var workingRoot = Path.Combine(appData, "work", $"import-{DateTime.UtcNow:yyyyMMdd_HHmmss}-{Guid.NewGuid():N}");
        var downloadedPath = Path.Combine(workingRoot, importPackage.AssetName);
        var preparedPath = Path.Combine(workingRoot, "MV-Craftoria-CurseForge-Import.zip");
        Directory.CreateDirectory(workingRoot);

        try
        {
            await releaseClient.DownloadImportPackageAsync(release, downloadedPath, progress, cancellationToken);
            progress?.Report(new UpdateProgress(56, "Verifying installer", "Checking signed CurseForge package"));
            var actualHash = await ComputeSha256Async(downloadedPath, cancellationToken);
            if (!string.Equals(actualHash, importPackage.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException("The CurseForge import package checksum does not match the signed release.");
            }

            progress?.Report(new UpdateProgress(58, "Preparing client", profileName));
            await PrepareNamedImportAsync(downloadedPath, preparedPath, profileName, release.Manifest.Version, cancellationToken);
            var previousPaths = locator.FindProfiles(configuration.ProductName)
                .Select(profile => profile.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            progress?.Report(new UpdateProgress(60, "Opening CurseForge", "Starting the official profile importer"));
            var restart = await CurseForgeProcessService.PrepareForMaintenanceAsync(cancellationToken);
            CurseForgeProcessService.Launch(restart, enableAccessibility: true);
            using var process = await CurseForgeProcessService.WaitForMainWindowAsync(cancellationToken);

            progress?.Report(new UpdateProgress(62, "Importing client", "Selecting the verified profile package"));
            await Task.Run(() => DriveImportUi(process, preparedPath, cancellationToken), cancellationToken);

            progress?.Report(new UpdateProgress(72, "CurseForge is installing", "Downloading mods and registering the client"));
            var imported = await WaitForImportedProfileAsync(profileName, previousPaths, process, progress, cancellationToken);
            progress?.Report(new UpdateProgress(100, "Client installed", $"{imported.Name} is now in My Modpacks"));
            AppLog.Info($"CurseForge imported and registered {imported.Name} at {imported.Path}");
            return imported;
        }
        finally
        {
            TryDeleteDirectory(workingRoot);
        }
    }

    private async Task<CurseForgeProfile> WaitForImportedProfileAsync(
        string profileName,
        HashSet<string> previousPaths,
        Process curseForge,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMinutes(30);
        var nextProgress = 74d;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            curseForge.Refresh();
            if (curseForge.HasExited) throw new InvalidOperationException("CurseForge closed before the client import finished.");

            var imported = locator.FindProfiles(configuration.ProductName)
                .FirstOrDefault(profile =>
                    !previousPaths.Contains(profile.Path) &&
                    string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase));
            if (imported is not null) return imported;

            nextProgress = Math.Min(94, nextProgress + 0.2);
            progress?.Report(new UpdateProgress(nextProgress, "CurseForge is installing", "Waiting for My Modpacks registration"));
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("CurseForge did not finish importing the new client within 30 minutes.");
    }

    private static void DriveImportUi(Process process, string archivePath, CancellationToken cancellationToken)
    {
        var root = AutomationElement.FromHandle(process.MainWindowHandle);
        var importButton = FindEnabled(root, "Import", ControlType.Button, TimeSpan.FromSeconds(30), cancellationToken);
        Invoke(importButton);

        var chooseButton = FindEnabled(root, "Choose .zip file", ControlType.Button, TimeSpan.FromSeconds(15), cancellationToken);
        Invoke(chooseButton);

        var fileDialog = FindEnabled(root, "Select File", ControlType.Window, TimeSpan.FromSeconds(15), cancellationToken);
        var dialogHandle = new IntPtr(fileDialog.Current.NativeWindowHandle);
        var fileNameHandle = GetDlgItem(dialogHandle, FileNameControlId);
        var openButtonHandle = GetDlgItem(dialogHandle, OpenButtonControlId);
        if (fileNameHandle == IntPtr.Zero || openButtonHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("CurseForge's file picker could not be controlled.");
        }
        if (!SetWindowText(fileNameHandle, archivePath))
        {
            throw new InvalidOperationException("The CurseForge import package could not be selected.");
        }
        SendMessage(openButtonHandle, ButtonClick, IntPtr.Zero, IntPtr.Zero);

        var safetyDeadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < safetyDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            var allFiles = TryFindEnabled(root, "All Files", ControlType.Button);
            if (allFiles is not null)
            {
                var checkbox = FindEnabled(
                    root,
                    "I understand that installing all files is at my own risk.",
                    ControlType.CheckBox,
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
                var toggle = (TogglePattern)checkbox.GetCurrentPattern(TogglePattern.Pattern);
                if (toggle.Current.ToggleState != ToggleState.On) toggle.Toggle();
                allFiles = FindEnabled(root, "All Files", ControlType.Button, TimeSpan.FromSeconds(10), cancellationToken);
                Invoke(allFiles);
                return;
            }

            if (TryFindEnabled(root, "Cancel", ControlType.Button) is null &&
                TryFindEnabled(root, "Choose .zip file", ControlType.Button) is null)
            {
                return;
            }
            Thread.Sleep(250);
        }
        throw new TimeoutException("CurseForge did not finish scanning the import package.");
    }

    private static AutomationElement FindEnabled(
        AutomationElement root,
        string name,
        ControlType controlType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = TryFindEnabled(root, name, controlType);
            if (element is not null) return element;
            Thread.Sleep(200);
        }
        throw new TimeoutException($"CurseForge control was not found: {name}");
    }

    private static AutomationElement? TryFindEnabled(AutomationElement root, string name, ControlType controlType)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, controlType),
            new PropertyCondition(AutomationElement.IsEnabledProperty, true));
        return root.FindFirst(TreeScope.Descendants, condition);
    }

    private static void Invoke(AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static async Task PrepareNamedImportAsync(
        string sourcePath,
        string destinationPath,
        string profileName,
        string version,
        CancellationToken cancellationToken)
    {
        await using var destinationStream = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1024 * 128, true);
        using var source = ZipFile.OpenRead(sourcePath);
        using var destination = new ZipArchive(destinationStream, ZipArchiveMode.Create, leaveOpen: true);
        var foundManifest = false;
        foreach (var entry in source.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(entry.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                foundManifest = true;
                await using var input = entry.Open();
                using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                var root = JsonNode.Parse(await reader.ReadToEndAsync(cancellationToken))?.AsObject()
                    ?? throw new InvalidDataException("The CurseForge import manifest is invalid.");
                root["name"] = profileName;
                root["version"] = version;
                var outputEntry = destination.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var output = outputEntry.Open();
                await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
                await writer.WriteAsync(root.ToJsonString(JsonDefaults.Options).AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                continue;
            }

            var copy = destination.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            copy.LastWriteTime = entry.LastWriteTime;
            if (entry.FullName.EndsWith('/')) continue;
            await using var sourceStream = entry.Open();
            await using var targetStream = copy.Open();
            await sourceStream.CopyToAsync(targetStream, cancellationToken);
        }
        if (!foundManifest) throw new InvalidDataException("The CurseForge import package has no manifest.json.");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception)
        {
            AppLog.Error($"Could not remove CurseForge import work directory: {path}", exception);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int controlId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowText(IntPtr window, string text);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
