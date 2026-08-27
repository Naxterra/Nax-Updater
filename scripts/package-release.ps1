[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.13.1'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$publishDirectory = Join-Path $artifactsRoot 'NaxUpdater-win-x64'
$installerDirectory = Join-Path $artifactsRoot 'installer'
$releaseDirectory = Join-Path $artifactsRoot 'release'

function Reset-ProjectDirectory([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repoRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside the repository: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

Reset-ProjectDirectory $publishDirectory
Reset-ProjectDirectory $installerDirectory
Reset-ProjectDirectory $releaseDirectory

dotnet build (Join-Path $repoRoot 'NaxUpdater.slnx') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }

dotnet run --project (Join-Path $repoRoot 'tests\NaxUpdater.Core.SmokeTests\NaxUpdater.Core.SmokeTests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }

dotnet publish (Join-Path $repoRoot 'src\NaxUpdater\NaxUpdater.csproj') -c Release -r win-x64 --self-contained true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }

dotnet build (Join-Path $repoRoot 'installer\NaxUpdater.Installer.wixproj') -c Release -p:NaxUpdaterVersion=$Version
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }

$portableArchive = Join-Path $releaseDirectory "NaxUpdater-$Version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portableArchive -CompressionLevel Optimal

$installers = @(
    @{ Culture = 'en-US'; Name = "NaxUpdater-$Version-Setup-x64-en-US.msi" },
    @{ Culture = 'de-DE'; Name = "NaxUpdater-$Version-Setup-x64-de-DE.msi" }
)
foreach ($installer in $installers) {
    $source = Join-Path $installerDirectory "$($installer.Culture)\NaxUpdater-Setup-x64.msi"
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Expected installer was not produced: $source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $releaseDirectory $installer.Name)
}

$releaseFiles = Get-ChildItem -LiteralPath $releaseDirectory -File | Sort-Object Name
$checksumLines = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$($file.Name)"
}
$checksumLines | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding utf8NoBOM

Get-ChildItem -LiteralPath $releaseDirectory -File |
    Sort-Object Name |
    Select-Object Name, Length, LastWriteTime
