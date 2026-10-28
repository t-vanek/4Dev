param(
    [Parameter(Mandatory=$true)][ValidateSet('official','ours')][string]$Client,
    [string]$Output = '.runtime\client-comparison-2026-10-27'
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$toolsRoot = Join-Path $repoRoot '.runtime\client-compare-tools'
$jdkRoot = Get-ChildItem -LiteralPath $toolsRoot -Directory | Where-Object Name -Like 'jdk-*' | Select-Object -First 1
$ghidraRoot = Get-ChildItem -LiteralPath $toolsRoot -Directory | Where-Object Name -Like 'ghidra_*_PUBLIC' | Select-Object -First 1
if (-not $jdkRoot -or -not $ghidraRoot) { throw 'Run setup_tools.py first.' }
$resultRoot = (Resolve-Path -LiteralPath (Join-Path $repoRoot $Output)).Path
# Ghidra project locators do not allow a path element starting with a dot.
$projectRoot = Join-Path $repoRoot 'build\client-compare-ghidra'
New-Item -ItemType Directory -Force -Path $projectRoot | Out-Null
$env:JAVA_HOME = $jdkRoot.FullName
$settingsRoot = Join-Path $toolsRoot ('ghidra-settings-' + $Client)
$env:GHIDRA_HEADLESS_JAVA_OPTIONS = "-Dapplication.settingsdir=$settingsRoot -Dsun.io.useCanonCaches=false"
$inputPath = if ($Client -eq 'official') { Join-Path $resultRoot 'TClient.official.analysis.exe' } else { Join-Path $repoRoot 'Game\TClient.exe' }
$projectName = if ($Client -eq 'official') { 'OfficialClient' } else { 'OurClient' }
& (Join-Path $ghidraRoot.FullName 'support\analyzeHeadless.bat') $projectRoot $projectName -import $inputPath -scriptPath $PSScriptRoot -postScript ExportComparison.java (Join-Path $resultRoot ('decompilation-' + $Client)) -analysisTimeoutPerFile 600 -max-cpu 4
if ($LASTEXITCODE -ne 0) { throw "Ghidra failed with exit code $LASTEXITCODE" }
