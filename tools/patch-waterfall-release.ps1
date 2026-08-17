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
$packageName = "MV-Craftoria-$Version.zip"
$importName = "MV-Craftoria-$Version-CurseForge-Import.zip"
$environmentPath = 'config/subtle_effects/environment.toml'
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

function Set-ZipTextEntry {
    param(
        [Parameter(Mandatory)] [string] $ArchivePath,
        [Parameter(Mandatory)] [string] $EntryPath,
        [Parameter(Mandatory)] [string] $Text
    )
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

function Get-ZipTextEntry {
    param([string] $ArchivePath, [string] $EntryPath)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($EntryPath)
        if ($null -eq $entry) { throw "Archive entry not found: $EntryPath" }
        $reader = [IO.StreamReader]::new($entry.Open(), [Text.Encoding]::UTF8, $true)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
}

function Get-Sha256Bytes([byte[]] $Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Force -Path $output | Out-Null

$package = Join-Path $output $packageName
$import = Join-Path $output $importName
Copy-Item -LiteralPath (Join-Path $source $packageName) -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $source $importName) -Destination $import -Force
Copy-Item -LiteralPath (Join-Path $source 'MV-Craftoria-Updater.exe') -Destination $output -Force

$packageEnvironmentEntry = "payload/$environmentPath"
$environment = Get-ZipTextEntry $package $packageEnvironmentEntry
if ($environment -notmatch '(?m)^waterfallsEnabled\s*=\s*(true|false)\s*$') {
    throw 'Subtle Effects waterfall setting was not found in the release package.'
}
$environment = [Text.RegularExpressions.Regex]::Replace(
    $environment,
    '(?m)^waterfallsEnabled\s*=\s*(true|false)\s*$',
    'waterfallsEnabled = false')
Set-ZipTextEntry $package $packageEnvironmentEntry $environment

$environmentBytes = $utf8.GetBytes($environment)
$patchText = Get-ZipTextEntry $package 'mv-patch.json'
$patch = $patchText | ConvertFrom-Json
$managedFile = @($patch.files | Where-Object { $_.path -eq $environmentPath })
if ($managedFile.Count -ne 1) { throw 'Release patch manifest has no unique Subtle Effects environment entry.' }
$managedFile[0].sha256 = Get-Sha256Bytes $environmentBytes
$managedFile[0].size = $environmentBytes.Length
Set-ZipTextEntry $package 'mv-patch.json' ($patch | ConvertTo-Json -Depth 8)

$importEntry = "overrides/$environmentPath"
$importEnvironment = Get-ZipTextEntry $import $importEntry
$importEnvironment = [Text.RegularExpressions.Regex]::Replace(
    $importEnvironment,
    '(?m)^waterfallsEnabled\s*=\s*(true|false)\s*$',
    'waterfallsEnabled = false')
Set-ZipTextEntry $import $importEntry $importEnvironment

$release = Get-Content -LiteralPath (Join-Path $source 'mv-release.json') -Raw | ConvertFrom-Json
$release.publishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
$waterfallNote = 'Disabled Subtle Effects waterfall particles to prevent severe FPS loss in water-heavy structures and boss arenas.'
$release.changelog = @($release.changelog | Where-Object { $_ -ne $waterfallNote }) + $waterfallNote
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

if ((Get-ZipTextEntry $package $packageEnvironmentEntry) -notmatch '(?m)^waterfallsEnabled\s*=\s*false\s*$') {
    throw 'Waterfall setting verification failed for the updater package.'
}
if ((Get-ZipTextEntry $import $importEntry) -notmatch '(?m)^waterfallsEnabled\s*=\s*false\s*$') {
    throw 'Waterfall setting verification failed for the CurseForge import package.'
}

Get-Item -LiteralPath $package, $import, $manifestPath, (Join-Path $output 'mv-release.sig'),
    (Join-Path $output 'MV-Craftoria-Updater.exe') |
    Select-Object Name, Length, FullName
