[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.17.27'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$publishDirectory = Join-Path $artifactsRoot 'NaxUpdater-win-x64'
$installerDirectory = Join-Path $artifactsRoot 'installer'
$bundleDirectory = Join-Path $artifactsRoot 'bundle'
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

# The application's own version (assembly, About dialog, User-Agent strings) comes
# from the project and the source; packaging another -Version would label installers
# with a number the application does not carry.
# Never override it with -p:Version: that also renumbers NaxUpdater.Core.dll
# (1.0.0.0 -> 0.17.27.0), and Windows Installer then treats the installed DLL as
# newer, skips it and removes it during the upgrade (0.17.27 shipped without it).
$projectVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src\NaxUpdater\NaxUpdater.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($projectVersion -ne $Version) {
    throw "Version mismatch: -Version is $Version but src\NaxUpdater\NaxUpdater.csproj says $projectVersion. Bump every version reference first (git grep the old version)."
}

Reset-ProjectDirectory $publishDirectory
Reset-ProjectDirectory $installerDirectory
Reset-ProjectDirectory $bundleDirectory
Reset-ProjectDirectory $releaseDirectory

dotnet build (Join-Path $repoRoot 'NaxUpdater.slnx') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }

dotnet run --project (Join-Path $repoRoot 'tests\NaxUpdater.Core.TransactionTests\NaxUpdater.Core.TransactionTests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Deterministic transaction tests failed.' }

dotnet run --project (Join-Path $repoRoot 'tests\NaxUpdater.Core.AlgorithmTests\NaxUpdater.Core.AlgorithmTests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Algorithm regression tests failed.' }

dotnet run --project (Join-Path $repoRoot 'tests\NaxUpdater.Core.SmokeTests\NaxUpdater.Core.SmokeTests.csproj') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }

dotnet publish (Join-Path $repoRoot 'src\NaxUpdater\NaxUpdater.csproj') -c Release -r win-x64 --self-contained true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
$publishedVersion = (Get-Item -LiteralPath (Join-Path $publishDirectory 'Nax-Updater.exe')).VersionInfo.ProductVersion
if (-not $publishedVersion -or -not ($publishedVersion -eq $Version -or $publishedVersion.StartsWith("$Version+"))) {
    throw "The published Nax-Updater.exe reports version '$publishedVersion' instead of $Version."
}

dotnet build (Join-Path $repoRoot 'installer\NaxUpdater.Installer.wixproj') -c Release -p:NaxUpdaterVersion=$Version
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }

dotnet build (Join-Path $repoRoot 'installer\NaxUpdater.Bundle.wixproj') -t:Rebuild -c Release -p:NaxUpdaterVersion=$Version
if ($LASTEXITCODE -ne 0) { throw 'Multilingual setup bundle build failed.' }

$portableArchive = Join-Path $releaseDirectory "NaxUpdater-$Version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $portableArchive -CompressionLevel Optimal

$bundle = Join-Path $bundleDirectory 'NaxUpdater-Setup-x64.exe'
if (-not (Test-Path -LiteralPath $bundle)) {
    throw "Expected multilingual setup bundle was not produced: $bundle"
}
Copy-Item -LiteralPath $bundle -Destination (Join-Path $releaseDirectory "NaxUpdater-$Version-Setup-x64.exe")

$releaseFiles = Get-ChildItem -LiteralPath $releaseDirectory -File | Sort-Object Name
$checksumLines = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$($file.Name)"
}
$checksumLines | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding utf8NoBOM

Get-ChildItem -LiteralPath $releaseDirectory -File |
    Sort-Object Name |
    Select-Object Name, Length, LastWriteTime
