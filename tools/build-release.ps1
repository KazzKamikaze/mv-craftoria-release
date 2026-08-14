[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $SourceDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string[]] $SupportedFrom = @('NOT_INSTALLED', '1.0.0-final'),
    [string] $Summary = 'A new MV Craftoria client release is available.',
    [string[]] $Changelog = @('Client files updated.'),
    [string[]] $Delete = @(),
    [string] $PrivateKeyPath = "$env:USERPROFILE\.mv-craftoria\keys\release-private.pem"
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$privateKey = [IO.Path]::GetFullPath($PrivateKeyPath)
if (-not (Test-Path -LiteralPath (Join-Path $source 'minecraftinstance.json') -PathType Leaf)) {
    throw 'SourceDirectory must be a complete CurseForge profile template containing minecraftinstance.json.'
}
if (-not (Test-Path -LiteralPath $privateKey -PathType Leaf)) { throw "Private signing key not found: $privateKey" }
New-Item -ItemType Directory -Force -Path $output | Out-Null

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
New-Item -ItemType Directory -Force -Path $payload | Out-Null
try {
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

    $patch = [ordered]@{
        schemaVersion = 1
        product = 'MV Craftoria'
        targetVersion = $Version
        supportedFrom = @($SupportedFrom)
        files = @($files)
        delete = @($Delete)
    }
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $session 'mv-patch.json'), ($patch | ConvertTo-Json -Depth 8), $utf8)

    $packageName = "MV-Craftoria-$Version.zip"
    $packagePath = Join-Path $output $packageName
    Compress-Archive -Path (Join-Path $session '*') -DestinationPath $packagePath -CompressionLevel Optimal -Force
    $packageInfo = Get-Item -LiteralPath $packagePath
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
    Get-Item -LiteralPath $packagePath, $manifestPath, (Join-Path $output 'mv-release.sig') |
        Select-Object Name, Length, FullName | Format-Table -AutoSize
} finally {
    if (Test-Path -LiteralPath $session) { Remove-Item -LiteralPath $session -Recurse -Force }
}
