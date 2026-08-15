[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\dist\updater')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\MvCraftoriaUpdater\MvCraftoriaUpdater.csproj'
$output = [IO.Path]::GetFullPath($OutputDirectory)
dotnet publish $project -c Release -r win-x64 --self-contained true -o $output
if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }
Write-Host "Updater published to $output" -ForegroundColor Green
