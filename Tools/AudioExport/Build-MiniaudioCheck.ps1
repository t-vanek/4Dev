param([string]$OutputDirectory = '.runtime/audio/tools')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ tools were not found.' }
$env:PATH = "$(Split-Path $vswhere);$env:PATH"
Import-Module "$vs/Common7/Tools/Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x86 -host_arch=x64' | Out-Null
& cl.exe /nologo /O2 /std:c++17 /EHsc /MT /W3 /utf-8 "/I$repo/Lib/3rdParty/miniaudio" "/Fe$output/MiniaudioCheck.exe" "/Fo$output/MiniaudioCheck.obj" "$PSScriptRoot/MiniaudioCheck.cpp"
if ($LASTEXITCODE) { throw 'MiniaudioCheck build failed.' }
