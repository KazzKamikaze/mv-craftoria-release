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
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint KeyboardInput = 1;
    private const uint KeyUp = 0x0002;
    private const uint UnicodeKey = 0x0004;
    private const ushort VirtualKeyA = 0x41;
    private const ushort VirtualKeyControl = 0x11;
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
            if (imported is not null &&
                !string.Equals(imported.Version, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                IsProfileReady(curseForge, profileName))
            {
                return imported;
            }

            nextProgress = Math.Min(94, nextProgress + 0.2);
            progress?.Report(new UpdateProgress(
                nextProgress,
                "CurseForge is installing",
                imported is null ? "Waiting for My Modpacks registration" : "Downloading and installing profile files"));
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
        SetForegroundWindow(dialogHandle);
        var fileName = FindByAutomationId(root, "1148", TimeSpan.FromSeconds(10), cancellationToken);
        ClickCenter(fileName);
        SendKeyChord(VirtualKeyControl, VirtualKeyA);
        SendUnicodeText(archivePath);
        var openButton = FindEnabled(root, "Open", ControlType.Pane, TimeSpan.FromSeconds(10), cancellationToken);
        ClickCenter(openButton);

        var safetyDeadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < safetyDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            var allFiles = TryFind(root, "All Files", ControlType.Button);
            if (allFiles is not null)
            {
                var checkboxText = FindEnabled(
                    root,
                    "I understand that installing all files is at my own risk.",
                    ControlType.Text,
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
                var checkbox = TreeWalker.RawViewWalker.GetParent(checkboxText)
                    ?? throw new InvalidOperationException("CurseForge's All Files confirmation could not be controlled.");
                ClickCenter(checkbox);
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

    private static AutomationElement? TryFind(AutomationElement root, string name, ControlType controlType)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));
        return root.FindFirst(TreeScope.Descendants, condition);
    }

    private static AutomationElement FindByAutomationId(
        AutomationElement root,
        string automationId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = root.FindFirst(TreeScope.Descendants, condition);
            if (element is not null) return element;
            Thread.Sleep(200);
        }
        throw new TimeoutException("CurseForge's file-name control was not found.");
    }

    private static bool IsProfileReady(Process process, string profileName)
    {
        try
        {
            var root = AutomationElement.FromHandle(process.MainWindowHandle);
            var buttons = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons)
            {
                var name = button.Current.Name;
                if (name.StartsWith(profileName + " ", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains(" Play", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Installing", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
            // CurseForge is refreshing the profile card; poll again.
        }
        return false;
    }

    private static void ClickCenter(AutomationElement element)
    {
        var rectangle = element.Current.BoundingRectangle;
        if (rectangle.IsEmpty) throw new InvalidOperationException("A required CurseForge control is not visible.");
        var x = checked((int)Math.Round(rectangle.Left + rectangle.Width / 2));
        var y = checked((int)Math.Round(rectangle.Top + rectangle.Height / 2));
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("The mouse could not be positioned over CurseForge.");
        mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    private static void SendKeyChord(ushort modifier, ushort key)
    {
        SendVirtualKey(modifier, keyUp: false);
        SendVirtualKey(key, keyUp: false);
        SendVirtualKey(key, keyUp: true);
        SendVirtualKey(modifier, keyUp: true);
    }

    private static void SendVirtualKey(ushort key, bool keyUp)
    {
        var input = new Input
        {
            Type = KeyboardInput,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInputData { VirtualKey = key, Flags = keyUp ? KeyUp : 0 }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new InvalidOperationException("Keyboard input could not be sent to CurseForge.");
        }
    }

    private static void SendUnicodeText(string value)
    {
        foreach (var character in value)
        {
            var down = new Input
            {
                Type = KeyboardInput,
                Data = new InputUnion
                {
                    Keyboard = new KeyboardInputData { ScanCode = character, Flags = UnicodeKey }
                }
            };
            var up = down;
            up.Data.Keyboard.Flags = UnicodeKey | KeyUp;
            if (SendInput(2, [down, up], Marshal.SizeOf<Input>()) != 2)
            {
                throw new InvalidOperationException("The CurseForge import path could not be entered.");
            }
        }
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] internal MouseInputData Mouse;
        [FieldOffset(0)] internal KeyboardInputData Keyboard;
        [FieldOffset(0)] internal HardwareInputData Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInputData
    {
        internal uint Message;
        internal ushort ParameterLow;
        internal ushort ParameterHigh;
    }
}
