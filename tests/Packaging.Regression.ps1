[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
$check = Join-Path $PSScriptRoot '..\scripts\Test-MsiVersion.ps1'
& $check -Path $MsiPath -Version $ExpectedVersion
Write-Output 'Test-MsiVersion::matches_expected_product_version PASS'
try {
    & $check -Path $MsiPath -Version '0.0.0'
    throw 'A mismatched MSI was accepted.'
}
catch {
    if ($_.Exception.Message -notmatch 'MSI version mismatch') { throw }
    Write-Output 'Test-MsiVersion::rejects_wrong_product_version PASS'
}
