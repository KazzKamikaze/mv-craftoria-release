[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $SourceDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $PackageFileName,
    [string[]] $SupportedFrom = @('NOT_INSTALLED', '1.0.0', '1.0.0-final'),
    [string] $Summary = 'A new MV Craftoria client release is available.',
    [string[]] $Changelog = @('Client files updated.'),
    [string[]] $Delete = @(),
    [string] $PrivateKeyPath = "$env:USERPROFILE\.mv-craftoria\keys\release-private.pem"
)

$ErrorActionPreference = 'Stop'
if ($Version.EndsWith('-final', [StringComparison]::OrdinalIgnoreCase)) {
    $Version = $Version.Substring(0, $Version.Length - '-final'.Length)
}
$source = [IO.Path]::GetFullPath($SourceDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$privateKey = [IO.Path]::GetFullPath($PrivateKeyPath)
if (-not (Test-Path -LiteralPath (Join-Path $source 'minecraftinstance.json') -PathType Leaf)) {
    throw 'SourceDirectory must be a complete CurseForge profile template containing minecraftinstance.json.'
}
if (-not (Test-Path -LiteralPath $privateKey -PathType Leaf)) { throw "Private signing key not found: $privateKey" }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$instance = Get-Content -LiteralPath (Join-Path $source 'minecraftinstance.json') -Raw | ConvertFrom-Json
if (-not $instance.manifest -or -not $instance.manifest.files) {
    throw 'minecraftinstance.json does not contain a CurseForge export manifest.'
}

$excludedDirectories = @(
    '.mv-update', '.mixin.out', '.vscode', 'backups', 'crash-reports', 'debug',
    'downloads', 'dynamic-resource-pack-cache', 'fancymenu_data', 'local', 'logs',
    'saves', 'schematics', 'screenshots', 'Distant_Horizons_server_data', 'ESM',
    'journeymap', 'XaeroWaypoints', 'XaeroWorldMap'
)
$excludedFiles = @(
    '.curseclient', 'command_history.txt', 'emi.json', 'observable_announce',
    'options.txt', 'optionsof.txt', 'patchouli_data.json', 'servers.dat',
    'servers.dat_old', 'usercache.json', 'usernamecache.json', 'launcher_log.txt'
)

$session = Join-Path $output ('.build-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $session 'payload'
$importRoot = Join-Path $session 'curseforge-import'
$importOverrides = Join-Path $importRoot 'overrides'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
New-Item -ItemType Directory -Force -Path $importOverrides | Out-Null
try {
    $trackedPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($addon in @($instance.installedAddons)) {
        foreach ($filePath in @($addon.filePaths)) {
            if ([string]::IsNullOrWhiteSpace($filePath)) { continue }
            $resolved = [IO.Path]::GetFullPath($filePath)
            if ($resolved.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                [void] $trackedPaths.Add([IO.Path]::GetRelativePath($source, $resolved).Replace('\', '/'))
            }
        }
    }

    $files = foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
        $parts = $relative.Split('/')
        if ($parts | Where-Object { $excludedDirectories -contains $_ }) { continue }
        if ($excludedFiles -contains $file.Name) { continue }

        $destination = Join-Path $payload $relative
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        [ordered]@{
            path = $relative
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            size = $file.Length
        }
    }

    $importExcludedFiles = @(
        '.curseclient', 'command_history.txt', 'emi.json', 'minecraftinstance.json',
        'observable_announce', 'patchouli_data.json', 'servers.dat_old', 'usercache.json',
        'usernamecache.json', 'launcher_log.txt'
    )
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
        $parts = $relative.Split('/')
        if ($parts | Where-Object { $excludedDirectories -contains $_ }) { continue }
        if ($importExcludedFiles -contains $file.Name) { continue }
        if ($trackedPaths.Contains($relative)) { continue }

        $destination = Join-Path $importOverrides $relative
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }

    $displayVersion = $Version
    $curseManifest = $instance.manifest | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    $curseManifest.name = "MV Craftoria $displayVersion"
    $curseManifest.version = $Version
    $curseManifest.author = 'MV'
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText(
        (Join-Path $importRoot 'manifest.json'),
        ($curseManifest | ConvertTo-Json -Depth 12),
        $utf8)
    $stateDirectory = Join-Path $importOverrides '.mv-update'
    New-Item -ItemType Directory -Force -Path $stateDirectory | Out-Null
    $initialState = [ordered]@{
        product = 'MV Craftoria'
        version = $Version
        previousVersion = 'NOT_INSTALLED'
        installedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        backupPath = ''
    }
    [IO.File]::WriteAllText(
        (Join-Path $stateDirectory 'state.json'),
        ($initialState | ConvertTo-Json -Depth 4),
        $utf8)

    $patch = [ordered]@{
        schemaVersion = 1
        product = 'MV Craftoria'
        targetVersion = $Version
        supportedFrom = @($SupportedFrom)
        files = @($files)
        delete = @($Delete)
    }
    [IO.File]::WriteAllText((Join-Path $session 'mv-patch.json'), ($patch | ConvertTo-Json -Depth 8), $utf8)

    $packageName = if ([string]::IsNullOrWhiteSpace($PackageFileName)) {
        "MV-Craftoria-$Version-UPDATER-DATA.zip"
    } else {
        [IO.Path]::GetFileName($PackageFileName)
    }
    if ([IO.Path]::GetExtension($packageName) -ne '.zip') {
        throw 'PackageFileName must use the .zip extension.'
    }
    $packagePath = Join-Path $output $packageName
    Compress-Archive -Path @(
        (Join-Path $session 'mv-patch.json'),
        $payload
    ) -DestinationPath $packagePath -CompressionLevel Optimal -Force
    $packageInfo = Get-Item -LiteralPath $packagePath
    $importPackageName = "MV-Craftoria-$displayVersion-MANUAL-INSTALL-CurseForge.zip"
    $importPackagePath = Join-Path $output $importPackageName
    Compress-Archive -Path (Join-Path $importRoot '*') -DestinationPath $importPackagePath -CompressionLevel Optimal -Force
    $importPackageInfo = Get-Item -LiteralPath $importPackagePath
    $release = [ordered]@{
        schemaVersion = 1
        product = 'MV Craftoria'
        version = $Version
        publishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        minimumUpdaterVersion = '1.0.0'
        summary = $Summary
        changelog = @($Changelog)
        supportedFrom = @($SupportedFrom)
        package = [ordered]@{
            assetName = $packageName
            sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
            size = $packageInfo.Length
        }
        importPackage = [ordered]@{
            assetName = $importPackageName
            sha256 = (Get-FileHash -LiteralPath $importPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
            size = $importPackageInfo.Length
        }
    }
    $manifestPath = Join-Path $output 'mv-release.json'
    [IO.File]::WriteAllText($manifestPath, ($release | ConvertTo-Json -Depth 8), $utf8)
    $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    $ecdsa = [Security.Cryptography.ECDsa]::Create()
    try {
        $ecdsa.ImportFromPem([IO.File]::ReadAllText($privateKey))
        $signature = $ecdsa.SignData($manifestBytes, [Security.Cryptography.HashAlgorithmName]::SHA256)
    } finally { $ecdsa.Dispose() }
    [IO.File]::WriteAllText((Join-Path $output 'mv-release.sig'), [Convert]::ToBase64String($signature), $utf8)

    Write-Host "GitHub release assets are ready:" -ForegroundColor Green
    Get-Item -LiteralPath $packagePath, $importPackagePath, $manifestPath, (Join-Path $output 'mv-release.sig') |
        Select-Object Name, Length, FullName | Format-Table -AutoSize
} finally {
    if (Test-Path -LiteralPath $session) { Remove-Item -LiteralPath $session -Recurse -Force }
}
