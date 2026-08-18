[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $UpdaterPath,
    [string] $Repository = 'KazzKamikaze/mv-craftoria-release'
)

$ErrorActionPreference = 'Stop'
throw 'Standalone updater publishing is disabled because replacing only the executable would invalidate its signed SHA-256 metadata. Rebuild the signed release with build-release.ps1 -UpdaterPath, then publish all assets with publish-release.ps1.'
