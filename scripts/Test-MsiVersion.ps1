[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)

$ErrorActionPreference = 'Stop'
$installer = $null
$database = $null
$view = $null
$record = $null
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.OpenDatabase([System.IO.Path]::GetFullPath($Path), 0)
    $view = $database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductVersion''')
    $view.Execute()
    $record = $view.Fetch()
    $actual = if ($null -ne $record) { $record.StringData(1) } else { '' }
    if ($actual -ne $Version) {
        throw "MSI version mismatch: '$Path' contains '$actual', expected '$Version'. Refusing to bundle a stale installer."
    }
}
finally {
    if ($null -ne $view) { $view.Close() }
    foreach ($item in @($record, $view, $database, $installer)) {
        if ($null -ne $item) { [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) }
    }
}
