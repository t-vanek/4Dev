$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
$previousFixture = $env:HAPPY_INTEROP_DLL
try {
    cargo test --locked
    if ($LASTEXITCODE -ne 0) { throw 'Rust tests failed.' }
    dotnet build tests/interop/Happy.Interop.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw '.NET interop fixture build failed.' }
    $env:HAPPY_INTEROP_DLL = Join-Path $PSScriptRoot 'tests/interop/bin/Release/net10.0/Happy.Interop.dll'
    cargo test --locked --test dotnet -- --ignored
    if ($LASTEXITCODE -ne 0) { throw '.NET interop tests failed.' }
} finally {
    $env:HAPPY_INTEROP_DLL = $previousFixture
    Pop-Location
}
