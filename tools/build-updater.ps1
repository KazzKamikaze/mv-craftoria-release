[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')] [string] $Repository,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\dist\updater')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\MvCraftoriaUpdater\MvCraftoriaUpdater.csproj'
$output = [IO.Path]::GetFullPath($OutputDirectory)
dotnet publish $project -c Release -r win-x64 --self-contained true -o $output
if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }
$configPath = Join-Path $output 'updater-config.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config.repository = $Repository
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Host "Updater published to $output" -ForegroundColor Green
