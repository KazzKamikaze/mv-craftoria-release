[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [Parameter(Mandatory)] [string] $Executable,
    [Parameter(Mandatory, ParameterSetName = 'Store')] [string] $CertificateThumbprint,
    [Parameter(Mandatory, ParameterSetName = 'Pfx')] [string] $PfxPath,
    [Parameter(ParameterSetName = 'Pfx')] [SecureString] $PfxPassword,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$signtool = Get-ChildItem -LiteralPath 'C:\Program Files (x86)\Windows Kits\10\bin' `
    -Filter signtool.exe -Recurse | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'Windows SDK signtool.exe is not installed.' }

$arguments = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
if ($PSCmdlet.ParameterSetName -eq 'Store') {
    $arguments += @('/sha1', $CertificateThumbprint)
} else {
    $arguments += @('/f', [IO.Path]::GetFullPath($PfxPath))
    if ($PfxPassword) {
        $plain = [Net.NetworkCredential]::new('', $PfxPassword).Password
        $arguments += @('/p', $plain)
    }
}
$arguments += [IO.Path]::GetFullPath($Executable)
& $signtool.FullName @arguments
if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed.' }
& $signtool.FullName verify /pa /v ([IO.Path]::GetFullPath($Executable))
if ($LASTEXITCODE -ne 0) { throw 'Authenticode verification failed.' }
