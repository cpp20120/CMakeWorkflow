param([Parameter(Mandatory)][string]$Cli)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$cliPath = (Resolve-Path $Cli).Path
$root = [System.IO.Directory]::CreateTempSubdirectory('cmwf-sdk-p4-').FullName
try {
    $runtime = Join-Path $root runtime
    & $cliPath project new runtime WinRuntime $runtime --variants
    Push-Location $runtime
    try {
        cmake --workflow --preset release
        & out/build/release/WinRuntime_bench.exe 10000
    } finally { Pop-Location }

    $interop = Join-Path $root interop
    & $cliPath project new interop WinInterop $interop --sdk-version 1.0.0 --variants
    Push-Location $interop
    try {
        cmake --preset debug -DBUILD_SHARED_LIBS=OFF
        cmake --build --preset debug
        ctest --preset debug
        cmake --preset release -DBUILD_SHARED_LIBS=ON -DCMWF_INTEROP_MANAGED_TEST=ON
        cmake --build --preset release
        ctest --preset release
        if (-not (Select-String 'ABI round trip: 42' out/build/release/Testing/Temporary/LastTest.log)) {
            throw 'The managed consumer did not call the shared DLL.'
        }
    } finally { Pop-Location }
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
