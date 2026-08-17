[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $AssetDirectory,
    [string] $Repository = 'KazzKamikaze/mv-craftoria-release',
    [string] $Title,
    [string] $NotesFile,
    [switch] $Prerelease,
    [switch] $ReplaceExisting
)

$ErrorActionPreference = 'Stop'
$assets = [IO.Path]::GetFullPath($AssetDirectory)
if (-not (Test-Path -LiteralPath $assets -PathType Container)) {
    throw "Asset directory not found: $assets"
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'Repository must use owner/name format.'
}

$required = @(
    "MV-Craftoria-$Version.zip",
    "MV-Craftoria-$Version-CurseForge-Import.zip",
    'mv-release.json',
    'mv-release.sig',
    'MV-Craftoria-Updater.exe'
)
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $assets $name) -PathType Leaf)) {
        throw "Required release asset is missing: $name"
    }
}

$credentialInput = "protocol=https`nhost=github.com`n`n"
$credentialLines = @($credentialInput | git credential fill)
$username = ($credentialLines | Where-Object { $_ -like 'username=*' } | Select-Object -First 1) -replace '^username=', ''
$token = ($credentialLines | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
if ([string]::IsNullOrWhiteSpace($username) -or [string]::IsNullOrWhiteSpace($token)) {
    throw 'GitHub credentials were not available from Git Credential Manager.'
}

$headers = @{
    Accept = 'application/vnd.github+json'
    Authorization = "Bearer $token"
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'MV-Craftoria-Release-Tool'
}
$tag = "v$Version"
$releaseTitle = if ([string]::IsNullOrWhiteSpace($Title)) { "MV Craftoria $Version" } else { $Title }
$notes = if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
    Get-Content -LiteralPath ([IO.Path]::GetFullPath($NotesFile)) -Raw
} else {
    "MV Craftoria $Version"
}

try {
    $existing = $null
    try {
        $existing = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repository/releases/tags/$tag"
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    }
    $payload = @{
        tag_name = $tag
        target_commitish = 'main'
        name = $releaseTitle
        body = $notes
        draft = $false
        prerelease = [bool]$Prerelease
    } | ConvertTo-Json

    $createdRelease = $false
    $replacingRelease = $null -ne $existing
    if ($null -ne $existing) {
        if (-not $ReplaceExisting) { throw "GitHub release $tag already exists." }
        if (-not $PSCmdlet.ShouldProcess("$Repository $tag", 'Replace GitHub release metadata and signed assets')) { return }

        $release = Invoke-RestMethod -Method Patch -Headers $headers -ContentType 'application/json' `
            -Body $payload -Uri "https://api.github.com/repos/$Repository/releases/$($existing.id)"
    } else {
        if (-not $PSCmdlet.ShouldProcess("$Repository $tag", 'Create GitHub release and upload signed assets')) { return }
        $release = Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' `
            -Body $payload -Uri "https://api.github.com/repos/$Repository/releases"
        $createdRelease = $true
    }
    $uploadBase = ($release.upload_url -replace '\{\?name,label\}$', '')
    $uploadedAssets = [Collections.Generic.List[object]]::new()
    $renamedOldAssets = [Collections.Generic.List[object]]::new()
    $uploadSuffix = '.pending-' + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

    try {
        foreach ($name in $required) {
            $path = Join-Path $assets $name
            $uploadName = if ($replacingRelease) { $name + $uploadSuffix } else { $name }
            $encodedName = [Uri]::EscapeDataString($uploadName)
            Write-Host "Uploading $name..." -ForegroundColor Cyan
            $uploaded = Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/octet-stream' `
                -InFile $path -Uri "${uploadBase}?name=$encodedName"
            if ($uploaded.size -ne (Get-Item -LiteralPath $path).Length) {
                throw "GitHub upload size mismatch for $name."
            }
            $uploadedAssets.Add([pscustomobject]@{ CanonicalName = $name; Asset = $uploaded })
        }

        if ($replacingRelease) {
            foreach ($old in @($existing.assets | Where-Object { $_.name -in $required })) {
                $previousName = $old.name + '.previous-' + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                $renameBody = @{ name = $previousName } | ConvertTo-Json
                Invoke-RestMethod -Method Patch -Headers $headers -ContentType 'application/json' `
                    -Body $renameBody `
                    -Uri "https://api.github.com/repos/$Repository/releases/assets/$($old.id)" | Out-Null
                $renamedOldAssets.Add([pscustomobject]@{ CanonicalName = $old.name; Asset = $old })
            }
            foreach ($pending in $uploadedAssets) {
                $renameBody = @{ name = $pending.CanonicalName } | ConvertTo-Json
                Invoke-RestMethod -Method Patch -Headers $headers -ContentType 'application/json' `
                    -Body $renameBody `
                    -Uri "https://api.github.com/repos/$Repository/releases/assets/$($pending.Asset.id)" | Out-Null
            }
            foreach ($previous in $renamedOldAssets) {
                try {
                    Invoke-RestMethod -Method Delete -Headers $headers `
                        -Uri "https://api.github.com/repos/$Repository/releases/assets/$($previous.Asset.id)" | Out-Null
                } catch {
                    Write-Warning "The replaced asset $($previous.CanonicalName) could not be removed."
                }
            }
        }
    } catch {
        foreach ($pending in $uploadedAssets) {
            try {
                Invoke-RestMethod -Method Delete -Headers $headers `
                    -Uri "https://api.github.com/repos/$Repository/releases/assets/$($pending.Asset.id)" | Out-Null
            } catch { }
        }
        foreach ($previous in $renamedOldAssets) {
            try {
                $renameBody = @{ name = $previous.CanonicalName } | ConvertTo-Json
                Invoke-RestMethod -Method Patch -Headers $headers -ContentType 'application/json' `
                    -Body $renameBody `
                    -Uri "https://api.github.com/repos/$Repository/releases/assets/$($previous.Asset.id)" | Out-Null
            } catch { }
        }
        if ($createdRelease) {
            Invoke-RestMethod -Method Delete -Headers $headers `
                -Uri "https://api.github.com/repos/$Repository/releases/$($release.id)" | Out-Null
        }
        throw
    }

    Write-Host "Published $($release.html_url)" -ForegroundColor Green
} finally {
    $token = $null
    $credentialLines = $null
    $headers = $null
}
