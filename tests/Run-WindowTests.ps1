[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $PSScriptRoot "NaxUpdater.Core.WindowTests\bin\$Configuration\net11.0-windows10.0.26100.0\win-x64\NaxUpdater.Core.WindowTests.exe"
$report = Join-Path $repoRoot 'artifacts\TestResults\windowed-store-query.json'
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
$process = Start-Process -FilePath $executable -ArgumentList @('--verify', ('"' + $report + '"')) -WindowStyle Hidden -Wait -PassThru
if (!(Test-Path -LiteralPath $report)) { throw "Windowed Store-query regression wrote no report (exit $($process.ExitCode))." }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (!$result.Passed -or !$result.NoConsole) { throw 'WindowTests::windowed_parent_decodes_store_utf8 FAIL: the fixture did not verify a console-less parent.' }
Write-Output "WindowTests::windowed_parent_decodes_store_utf8 PASS (host code page $($result.HostCodePage))."
if (!$result.QuestionPassed) { throw "WindowTests::$($result.QuestionScenario) FAIL: the query waited for input ($($result.QuestionSeconds) s, exit $($result.QuestionExitCode))." }
Write-Output "WindowTests::$($result.QuestionScenario) PASS ($($result.QuestionSeconds) s)."
if ($process.ExitCode -ne 0) { throw "Windowed regression failed (exit $($process.ExitCode))." }
