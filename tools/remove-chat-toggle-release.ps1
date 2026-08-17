[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SourceAssetDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $Version = '1.1.0',
    [string] $PrivateKeyPath = "$env:USERPROFILE\.mv-craftoria\keys\release-private.pem"
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceAssetDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$privateKey = [IO.Path]::GetFullPath($PrivateKeyPath)
$packageName = "MV-Craftoria-$Version-UPDATER-DATA.zip"
$importName = "MV-Craftoria-$Version-MANUAL-INSTALL-CurseForge.zip"
$modPath = 'mods/chattoggle-5.0.1+1.21-neoforge.jar'
$configPath = 'config/chattoggle.json'
$projectId = 737481
$utf8 = [Text.UTF8Encoding]::new($false)

foreach ($path in @(
    (Join-Path $source $packageName),
    (Join-Path $source $importName),
    (Join-Path $source 'mv-release.json'),
    (Join-Path $source 'MV-Craftoria-Updater.exe'),
    $privateKey
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required file not found: $path" }
}

function Get-ZipTextEntry([string] $ArchivePath, [string] $EntryPath) {
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($EntryPath)
        if ($null -eq $entry) { throw "Archive entry not found: $EntryPath" }
        $reader = [IO.StreamReader]::new($entry.Open(), [Text.Encoding]::UTF8, $true)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
}

function Set-ZipTextEntry([string] $ArchivePath, [string] $EntryPath, [string] $Text) {
    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry($EntryPath)
        if ($null -eq $entry) { throw "Archive entry not found: $EntryPath" }
        $entry.Delete()
        $replacement = $archive.CreateEntry($EntryPath, [IO.Compression.CompressionLevel]::Optimal)
        $stream = $replacement.Open()
        try {
            $bytes = $utf8.GetBytes($Text)
            $stream.Write($bytes, 0, $bytes.Length)
        } finally { $stream.Dispose() }
    } finally { $archive.Dispose() }
}

function Remove-ZipEntry([string] $ArchivePath, [string] $EntryPath) {
    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry($EntryPath)
        if ($null -ne $entry) { $entry.Delete() }
    } finally { $archive.Dispose() }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force -Path $output | Out-Null

$package = Join-Path $output $packageName
$import = Join-Path $output $importName
Copy-Item -LiteralPath (Join-Path $source $packageName) -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $source $importName) -Destination $import -Force
Copy-Item -LiteralPath (Join-Path $source 'MV-Craftoria-Updater.exe') -Destination $output -Force

$instanceEntry = 'payload/minecraftinstance.json'
$instance = Get-ZipTextEntry $package $instanceEntry | ConvertFrom-Json
$instance.installedAddons = @($instance.installedAddons | Where-Object {
    $_.addonID -ne $projectId -and $_.name -ne 'Chat Toggle'
})
$instance.manifest.files = @($instance.manifest.files | Where-Object { $_.projectID -ne $projectId })
$instanceText = $instance | ConvertTo-Json -Depth 100
$instanceBytes = $utf8.GetBytes($instanceText)
Set-ZipTextEntry $package $instanceEntry $instanceText

$patch = Get-ZipTextEntry $package 'mv-patch.json' | ConvertFrom-Json
$removedPaths = @($modPath, $configPath)
$patch.files = @($patch.files | Where-Object { $_.path -notin $removedPaths })
$instanceFile = @($patch.files | Where-Object { $_.path -eq 'minecraftinstance.json' })
if ($instanceFile.Count -ne 1) { throw 'Updater manifest has no unique minecraftinstance.json entry.' }
$instanceFile[0].sha256 = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData($instanceBytes)).ToLowerInvariant()
$instanceFile[0].size = $instanceBytes.Length
$patch.delete = @($patch.delete + $removedPaths | Sort-Object -Unique)
Set-ZipTextEntry $package 'mv-patch.json' ($patch | ConvertTo-Json -Depth 8)
Remove-ZipEntry $package "payload/$modPath"
Remove-ZipEntry $package "payload/$configPath"

$importManifest = Get-ZipTextEntry $import 'manifest.json' | ConvertFrom-Json
$importManifest.files = @($importManifest.files | Where-Object { $_.projectID -ne $projectId })
Set-ZipTextEntry $import 'manifest.json' ($importManifest | ConvertTo-Json -Depth 20)
Remove-ZipEntry $import "overrides/$configPath"

$release = Get-Content -LiteralPath (Join-Path $source 'mv-release.json') -Raw | ConvertFrom-Json
$release.publishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
$note = 'Removed Chat Toggle so the Y key can no longer redirect messages into FTB Teams chat.'
$release.changelog = @($release.changelog | Where-Object { $_ -ne $note }) + $note
$release.package.sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
$release.package.size = (Get-Item -LiteralPath $package).Length
$release.importPackage.sha256 = (Get-FileHash -LiteralPath $import -Algorithm SHA256).Hash.ToLowerInvariant()
$release.importPackage.size = (Get-Item -LiteralPath $import).Length
$manifestPath = Join-Path $output 'mv-release.json'
[IO.File]::WriteAllText($manifestPath, ($release | ConvertTo-Json -Depth 8), $utf8)

$ecdsa = [Security.Cryptography.ECDsa]::Create()
try {
    $ecdsa.ImportFromPem([IO.File]::ReadAllText($privateKey))
    $signature = $ecdsa.SignData(
        [IO.File]::ReadAllBytes($manifestPath),
        [Security.Cryptography.HashAlgorithmName]::SHA256)
} finally { $ecdsa.Dispose() }
[IO.File]::WriteAllText(
    (Join-Path $output 'mv-release.sig'),
    [Convert]::ToBase64String($signature),
    $utf8)

$verifiedPatch = Get-ZipTextEntry $package 'mv-patch.json' | ConvertFrom-Json
if (@($verifiedPatch.files | Where-Object { $_.path -in $removedPaths }).Count -ne 0) {
    throw 'Chat Toggle remains in the updater file manifest.'
}
if (@($removedPaths | Where-Object { $_ -notin $verifiedPatch.delete }).Count -ne 0) {
    throw 'Chat Toggle removal paths are missing from the updater manifest.'
}
$verifiedImport = Get-ZipTextEntry $import 'manifest.json' | ConvertFrom-Json
if (@($verifiedImport.files | Where-Object { $_.projectID -eq $projectId }).Count -ne 0) {
    throw 'Chat Toggle remains in the CurseForge import manifest.'
}

Get-Item -LiteralPath $package, $import, $manifestPath, (Join-Path $output 'mv-release.sig'),
    (Join-Path $output 'MV-Craftoria-Updater.exe') |
    Select-Object Name, Length, FullName
