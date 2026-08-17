[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $UpdaterPath,
    [string] $Repository = 'KazzKamikaze/mv-craftoria-release'
)

$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($UpdaterPath)
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Updater executable not found: $source"
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'Repository must use owner/name format.'
}

$canonicalName = 'MV-Craftoria-Updater.exe'
$temporaryName = "MV-Craftoria-Updater-$Version-upload.exe"
$credentialInput = "protocol=https`nhost=github.com`n`n"
$credentialLines = @($credentialInput | git credential fill)
$token = ($credentialLines | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
if ([string]::IsNullOrWhiteSpace($token)) {
    throw 'GitHub credentials were not available from Git Credential Manager.'
}

$headers = @{
    Accept = 'application/vnd.github+json'
    Authorization = "Bearer $token"
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'MV-Craftoria-Updater-Publisher'
}
$tag = "v$Version"
$download = Join-Path $env:TEMP ('.mv-updater-verify-' + [guid]::NewGuid().ToString('N') + '.exe')

try {
    $release = Invoke-RestMethod -Headers $headers `
        -Uri "https://api.github.com/repos/$Repository/releases/tags/$tag"
    if (-not $PSCmdlet.ShouldProcess("$Repository $tag", 'Replace updater executable')) { return }

    foreach ($stale in @($release.assets | Where-Object { $_.name -eq $temporaryName })) {
        Invoke-RestMethod -Method Delete -Headers $headers `
            -Uri "https://api.github.com/repos/$Repository/releases/assets/$($stale.id)" | Out-Null
    }

    $uploadBase = $release.upload_url -replace '\{\?name,label\}$', ''
    $encodedName = [Uri]::EscapeDataString($temporaryName)
    $uploaded = Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/octet-stream' `
        -InFile $source -Uri "${uploadBase}?name=$encodedName"
    if ($uploaded.size -ne (Get-Item -LiteralPath $source).Length) {
        throw 'Temporary GitHub upload size mismatch.'
    }

    foreach ($old in @($release.assets | Where-Object { $_.name -eq $canonicalName })) {
        Invoke-RestMethod -Method Delete -Headers $headers `
            -Uri "https://api.github.com/repos/$Repository/releases/assets/$($old.id)" | Out-Null
    }

    $renameBody = @{ name = $canonicalName } | ConvertTo-Json
    $published = Invoke-RestMethod -Method Patch -Headers $headers -ContentType 'application/json' `
        -Body $renameBody -Uri "https://api.github.com/repos/$Repository/releases/assets/$($uploaded.id)"

    Invoke-WebRequest -Headers @{ 'User-Agent' = 'MV-Craftoria-Updater-Verifier' } `
        -Uri $published.browser_download_url -OutFile $download
    $localHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $downloadHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
    if ($localHash -ne $downloadHash) {
        throw 'Downloaded GitHub updater hash mismatch.'
    }

    [pscustomobject]@{
        Asset = $published.name
        Size = $published.size
        SHA256 = $downloadHash
        Download = $published.browser_download_url
    }
} finally {
    Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue
    $token = $null
    $credentialLines = $null
    $headers = $null
}
