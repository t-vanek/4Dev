# Builds the client with CMake + Ninja inside a Visual Studio developer shell of the preset's
# architecture (x86, or x64 for the *-x64 presets).
#   .\Client\build.ps1 -Preset clang-x64          # 64-bit clang-cl build into build\clang-x64\bin\
#   .\Client\build.ps1                  # Release build into build\msvc-x86\bin\TClient.exe
#   .\Client\build.ps1 -Preset msvc-x86 -Deploy   # also copies TClient.exe into Game\
#   .\Client\build.ps1 -Preset clang-x86          # clang-cl build into build\clang-x86\bin\
#   .\Client\build.ps1 -Preset clang-x64 -KeepGoing   # builds every file it can: all the errors
param(
    [string]$Preset = "msvc-x86",
    [switch]$Deploy,
    [switch]$Clean,
    [switch]$KeepGoing
)
$ErrorActionPreference = "Stop"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio with the C++ x86/x64 tools was not found." }

# Enter the developer environment of the target (cl, rc, link, INCLUDE/LIB with ATL, cmake, ninja).
# VsDevCmd itself calls vswhere, so it must be on PATH.
$arch = if ($Preset -like "*-x64") { "amd64" } else { "x86" }
$env:PATH = "$(Split-Path $vswhere);$env:PATH"
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=$arch -host_arch=x64" | Out-Null
# clang-cl / lld-link for the clang presets (64-bit host tools; the preset names the target).
$llvm = "$vs\VC\Tools\Llvm\x64\bin"
if (Test-Path $llvm) { $env:PATH = "$llvm;$env:PATH" }

$originalCodePage = [int]([regex]::Match((& chcp.com), '\d+').Value)
Push-Location $PSScriptRoot
try {
    # CMake reads UTF-8. Matching cl's localized /showIncludes output keeps Ninja's header
    # dependencies correct even when only a non-English Visual Studio language pack is installed.
    chcp.com 65001 | Out-Null
    $buildRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\build'))
    $binaryDir = [IO.Path]::GetFullPath((Join-Path $buildRoot $Preset))
    if ($Clean -and (Test-Path -LiteralPath $binaryDir)) {
        if (-not $binaryDir.StartsWith($buildRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The clean target must be a preset directory inside the workspace build folder.'
        }
        Remove-Item -LiteralPath $binaryDir -Recurse -Force
    }

    $deployFlag = if ($Deploy) { "ON" } else { "OFF" }
    cmake --preset $Preset "-DTCLIENT_DEPLOY_TO_GAME=$deployFlag"
    if ($LASTEXITCODE) { throw "CMake configure failed." }
    if ($KeepGoing) { cmake --build --preset $Preset -- -k 0 } else { cmake --build --preset $Preset }
    if ($LASTEXITCODE) { throw "Build failed." }
} finally {
    chcp.com $originalCodePage | Out-Null
    Pop-Location
}
