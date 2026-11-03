#requires -Version 5.1
<#
.SYNOPSIS
  End-to-end smoke test for CMakeWorkflow's explicit Windows toolchain binding.
.DESCRIPTION
  Selects an installed Visual Studio compiler, Windows SDK, CMake and Ninja;
  generates a fresh workflow manifest with candidate IDs; executes
  configure -> build -> run -> CTest; verifies CMakeCache and run.json; then
  checks that changing the generator cannot silently rewrite that build tree.
  Uses vswhere/Windows SDK registry for discovery and exactly the CandidateId
  hash formula used by WindowsToolchains.CandidateId(). The application itself
  performs authoritative discovery/resolution again before execution.
.EXAMPLE
  .\scripts\windows-test.ps1 -RepoRoot C:\Code\CMakeWorkflow
.EXAMPLE
  .\scripts\windows-test.ps1 -Compiler clang-cl -SkipDriftTest
#>
[CmdletBinding()]
param(
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [ValidateSet('msvc', 'clang-cl')]
    [string] $Compiler = 'msvc',
    [string] $VsInstanceId,
    [switch] $SkipDriftTest,
    [switch] $Rebuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require-File([string] $Path, [string] $Description)
{
    if (-not [System.IO.File]::Exists($Path))
    {
        throw "$Description not found: $Path"
    }
    return [System.IO.Path]::GetFullPath($Path)
}

function Candidate-Id([string] $Role, [string] $Path)
{
    # C#: SHA256(UTF8(role + '\0' + Path.GetFullPath(path).ToUpperInvariant()))[..24].ToLowerInvariant()
    $canonical = [System.IO.Path]::GetFullPath($Path).ToUpperInvariant()
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Role + [char]0 + $canonical)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try
    {
        $digest = $sha.ComputeHash($bytes)
        return [System.BitConverter]::ToString($digest).Replace('-', '').Substring(0, 24).ToLowerInvariant()
    }
    finally
    {
        $sha.Dispose()
    }
}

function Find-Executable([string] $Name, [string[]] $Fallbacks)
{
    # WindowsToolchains discovers PATH exe entries, standalone CMake, and VS bundled tools.
    $commands = @(Get-Command -Name $Name -CommandType Application -All -ErrorAction SilentlyContinue)
    foreach ($command in $commands)
    {
        if ( [System.IO.File]::Exists($command.Source))
        {
            return [System.IO.Path]::GetFullPath($command.Source)
        }
    }
    foreach ($path in $Fallbacks)
    {
        if ($path -and [System.IO.File]::Exists($path))
        {
            return [System.IO.Path]::GetFullPath($path)
        }
    }
    throw "Cannot discover $Name. Install it or add its directory to PATH."
}

function Cache-Value([string] $Cache, [string] $Name)
{
    $prefix = $Name + ':'
    foreach ($line in [System.IO.File]::ReadLines($Cache))
    {
        if ( $line.StartsWith($prefix, [System.StringComparison]::Ordinal))
        {
            $index = $line.IndexOf('=')
            if ($index -ge 0)
            {
                return $line.Substring($index + 1)
            }
        }
    }
    throw "CMakeCache missing: $Name"
}

function Assert-SamePath([string] $Actual, [string] $Expected, [string] $Label)
{
    # CMake commonly stores forward slashes in Windows paths.
    $a = [System.IO.Path]::GetFullPath($Actual.Replace('/', '\'))
    $b = [System.IO.Path]::GetFullPath($Expected.Replace('/', '\'))
    if (-not [string]::Equals($a, $b, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "$Label mismatch: cache='$Actual', selected='$Expected'"
    }
    Write-Host "  PASS $Label => $a" -ForegroundColor Green
}

function Save-Workflow([string] $Path, [string] $Generator, [string] $SourceDir,
                       [string] $BuildDir, [hashtable] $Binding)
{
    # Keep generator identity consistent both in context and in toolchain binding.
    $toolchain = @{ }
    foreach ($key in $Binding.Keys)
    {
        $toolchain[$key] = $Binding[$key]
    }
    $toolchain.generator = $Generator
    $workflow = [ordered]@{
    schemaVersion = 2
    contexts = [ordered]@{
    hello = [ordered]@{
    sourceDirectory = $SourceDir
    buildDirectory = $BuildDir
    configuration = 'Debug'
    mode = 'managed'
    generator       = $Generator
    provenancePolicy = 'strict'
    toolchain = $toolchain
    # Intentionally no configurePreset: it is incompatible with explicit binding.
    }
    }
    nodes = @(
    [ordered]@{
    id = 'configure'; kind = 'configure'; context ='hello'
    },
    [ordered]@{
    id = 'build'; kind = 'build'; context ='hello'; target = 'hello'; dependsOn = @('configure')
    },
    [ordered]@{
    id = 'run'; kind = 'run'; from ='build'
    },
    [ordered]@{
    id = 'test'; kind = 'ctest'; context ='hello'; labelRegex = 'smoke'; dependsOn = @('build')
    }
    )
    }
    $json = ConvertTo-Json -InputObject $workflow -Depth 14
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

if (-not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess)
{
    throw 'Use 64-bit PowerShell on x64 Windows (the current toolchain execution adapter is x64 -> x64).'
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT)
{
    throw 'This smoke test requires Windows.'
}

$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
$project = Require-File (Join-Path $RepoRoot 'CMakeWorkflow.csproj') 'CMakeWorkflow project'
$sourceDir = Join-Path $RepoRoot 'examples\hello'
$cmakeLists = Require-File (Join-Path $sourceDir 'CMakeLists.txt') 'Hello CMake example'
$dotnet = Find-Executable 'dotnet.exe' @()

$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$vswhere = Find-Executable 'vswhere.exe' @(
(Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'),
(Join-Path $programFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
)
$vsJson = (& $vswhere -products '*' -prerelease -format json -utf8 -nologo | Out-String)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($vsJson)) {
throw 'vswhere failed or reported no installations.'
}
$installations = @($vsJson | ConvertFrom-Json | Sort-Object -Property installationVersion -Descending)
$chosenVs = $null
$compilerExe = $null
$msvcVersion = $null
foreach ($vs in $installations) {
$id = [string]$vs.instanceId
if ($VsInstanceId -and $id -ne $VsInstanceId) {
continue
}
$location = [string]$vs.installationPath
if (-not $location) {
continue
}
$vsDevCmd = Join-Path $location 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $vsDevCmd -PathType Leaf)) {
continue
}

if ($Compiler -eq 'msvc') {
$vcRoot = Join-Path $location 'VC\Tools\MSVC'
if (-not (Test-Path -LiteralPath $vcRoot -PathType Container)) {
continue
}
foreach ($toolset in @(Get-ChildItem -LiteralPath $vcRoot -Directory | Sort-Object -Property Name -Descending)) {
$candidate = Join-Path $toolset.FullName 'bin\Hostx64\x64\cl.exe'
if (Test-Path -LiteralPath $candidate -PathType Leaf) {
$compilerExe = [System.IO.Path]::GetFullPath($candidate)
$msvcVersion = $toolset.Name
break
}
}
}
else {
foreach ($tail in @('VC\Tools\Llvm\bin\clang-cl.exe', 'VC\Tools\Llvm\x64\bin\clang-cl.exe')) {
$candidate = Join-Path $location $tail
if (Test-Path -LiteralPath $candidate -PathType Leaf) {
$compilerExe = [System.IO.Path]::GetFullPath($candidate)
break
}
}
}
if ($compilerExe) {
$chosenVs = $vs; break
}
}
if ($null -eq $chosenVs) {
throw "No VS instance with VsDevCmd and $Compiler (x64) found. Install C++ desktop workload, or pass -VsInstanceId."
}

$vsPath = [string]$chosenVs.installationPath
$bundle = Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\CMake'
$cmakeExe = Find-Executable 'cmake.exe' @(
(Join-Path $programFiles 'CMake\bin\cmake.exe'),
(Join-Path $programFilesX86 'CMake\bin\cmake.exe'),
(Join-Path $bundle 'CMake\bin\cmake.exe')
)
$ninjaExe = Find-Executable 'ninja.exe' @((Join-Path $bundle 'Ninja\ninja.exe'))

$kitsKey = 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots'
try {
$kitsRoot = [string](Get-ItemProperty -LiteralPath $kitsKey -ErrorAction Stop).KitsRoot10
}
catch {
throw "Cannot find Windows 10/11 SDK registry root ($kitsKey): $_"
}
if (-not $kitsRoot) {
throw 'Windows SDK KitsRoot10 is missing.'
}
$sdkVersion = $null
$sdkPath = $null
$includes = Join-Path $kitsRoot 'Include'
if (-not (Test-Path -LiteralPath $includes -PathType Container)) {
throw 'Windows SDK Include directory missing.'
}
foreach ($dir in @(Get-ChildItem -LiteralPath $includes -Directory | Sort-Object -Property Name -Descending)) {
$lib = Join-Path $kitsRoot ('Lib\' + $dir.Name + '\um\x64\kernel32.lib')
if ((Test-Path -LiteralPath (Join-Path $dir.FullName 'um') -PathType Container) -and
(Test-Path -LiteralPath (Join-Path $dir.FullName 'ucrt') -PathType Container) -and
(Test-Path -LiteralPath $lib -PathType Leaf)) {
$sdkVersion = $dir.Name
$sdkPath = $dir.FullName
break
}
}
if (-not $sdkPath) {
throw 'No x64 Windows SDK with headers and kernel32.lib discovered.'
}

$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$workDir = Join-Path $RepoRoot ('out\windows-toolchain-smoke\' + $stamp)
$buildDir = Join-Path $workDir 'build-msvc-ninja'
$resultsDir = Join-Path $workDir 'results'
[void](New-Item -ItemType Directory -Path $workDir -Force)

$binding = @{
cmakeCandidateId = Candidate-Id 'cmake' $cmakeExe
ninjaCandidateId = Candidate-Id 'ninja' $ninjaExe
compilerCandidateId = Candidate-Id 'compiler' $compilerExe
sdkCandidateId = Candidate-Id 'sdk' $sdkPath
environmentProfileId = 'vs:' + [string]$chosenVs.instanceId
hostArchitecture = 'x64'
targetArchitecture = 'x64'
}
$manifestPath = Join-Path $workDir 'workflow-msvc.json'
Save-Workflow $manifestPath 'Ninja' $sourceDir $buildDir $binding

Write-Host "`nWindows toolchain smoke test" -ForegroundColor Cyan
Write-Host "  VS:       $($chosenVs.displayName) [$($chosenVs.instanceId)]"
Write-Host "  Compiler: $compilerExe"
if ($msvcVersion) {
Write-Host "  Toolset:  $msvcVersion"
}
Write-Host "  SDK:      $sdkVersion ($sdkPath)"
Write-Host "  CMake:    $cmakeExe"
Write-Host "  Ninja:    $ninjaExe"
Write-Host "  Manifest: $manifestPath"
Write-Host "  Output:   $workDir"
Write-Host '  NOTE: candidate IDs are re-resolved by CMakeWorkflow before execution.'

$cliDll = Join-Path $RepoRoot 'bin\Release\net10.0\CMakeWorkflow.dll'
if ($Rebuild -or -not [IO.File]::Exists($cliDll)) {
Write-Host "`nBuilding CMakeWorkflow CLI..." -ForegroundColor Cyan
& $dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) {
throw 'dotnet build failed.'
}
}
[void](Require-File $cliDll 'CMakeWorkflow CLI DLL')

Write-Host "`nRunning configure -> build -> run -> CTest..." -ForegroundColor Cyan
& $dotnet $cliDll workflow run $manifestPath --results-dir $resultsDir --json --no-history
if ($LASTEXITCODE -ne 0) {
throw "Workflow failed with exit code $LASTEXITCODE. Workdir: $workDir"
}

$cache = Require-File (Join-Path $buildDir 'CMakeCache.txt') 'CMake cache'
$cacheGenerator = Cache-Value $cache 'CMAKE_GENERATOR'
if ($cacheGenerator -ne 'Ninja') {
throw "Unexpected CMake generator: $cacheGenerator"
}
Write-Host "  PASS generator => $cacheGenerator" -ForegroundColor Green
Assert-SamePath (Cache-Value $cache 'CMAKE_CXX_COMPILER') $compilerExe 'C++ compiler'
Assert-SamePath (Cache-Value $cache 'CMAKE_MAKE_PROGRAM') $ninjaExe 'Ninja'

$reports = @(Get-ChildItem -LiteralPath $resultsDir -Recurse -Filter 'run.json' -File)
if ($reports.Count -ne 1) {
throw "Expected one run.json; found $($reports.Count) under $resultsDir"
}
$report = Get-Content -LiteralPath $reports[0].FullName -Raw | ConvertFrom-Json
if ($report.status -ne 'succeeded') {
throw "Run status is '$($report.status)', expected succeeded."
}
foreach ($nodeName in @('configure', 'build', 'run', 'test')) {
$matches = @($report.nodes | Where-Object {
$_.id -eq $nodeName -and $_.status -eq 'succeeded'
})
if ($matches.Count -ne 1) {
throw "Node '$nodeName' was not succeeded."
}
}
$identities = @($report.toolchains)
if ($identities.Count -ne 1 -or -not $identities[0].fingerprint) {
throw 'run.json has no valid toolchain identity/fingerprint.'
}
Assert-SamePath $identities[0].compiler $compilerExe 'Recorded compiler'
Write-Host '  PASS configure, build, run, CTest, and provenance' -ForegroundColor Green

if (-not $SkipDriftTest) {
# When a second MSVC-ABI compiler is installed in the same VS instance,
# verify that even a valid alternate compiler cannot reuse the build tree.
$otherCompiler = $null
if ($Compiler -eq 'msvc') {
foreach ($tail in @('VC\Tools\Llvm\bin\clang-cl.exe', 'VC\Tools\Llvm\x64\bin\clang-cl.exe')) {
$candidate = Join-Path $vsPath $tail
if (Test-Path -LiteralPath $candidate -PathType Leaf) {
$otherCompiler = $candidate; break
}
}
}
else {
$vcRoot = Join-Path $vsPath 'VC\Tools\MSVC'
if (Test-Path -LiteralPath $vcRoot -PathType Container) {
foreach ($toolset in @(Get-ChildItem -LiteralPath $vcRoot -Directory | Sort-Object Name -Descending)) {
$candidate = Join-Path $toolset.FullName 'bin\Hostx64\x64\cl.exe'
if (Test-Path -LiteralPath $candidate -PathType Leaf) {
$otherCompiler = $candidate; break
}
}
}
}
if ($otherCompiler) {
Write-Host "`nTesting refusal to replace compiler in existing build tree..." -ForegroundColor Cyan
$alternate = @{
}
foreach ($key in $binding.Keys) {
$alternate[$key] = $binding[$key]
}
$alternate.compilerCandidateId = Candidate-Id 'compiler' $otherCompiler
$alternateManifest = Join-Path $workDir 'workflow-compiler-drift.json'
Save-Workflow $alternateManifest 'Ninja' $sourceDir $buildDir $alternate
$before = (Get-FileHash -LiteralPath $cache -Algorithm SHA256).Hash
& $dotnet $cliDll workflow run $alternateManifest --results-dir (Join-Path $workDir 'results-compiler-drift') --json --no-history
$alternateExit = $LASTEXITCODE
if ($alternateExit -eq 0) {
throw 'DANGEROUS: compiler drift was accepted.'
}
if ($before -ne (Get-FileHash -LiteralPath $cache -Algorithm SHA256).Hash) {
throw 'DANGEROUS: CMakeCache.txt changed during compiler drift test.'
}
Write-Host "  PASS alternate compiler refused (exit $alternateExit); CMakeCache.txt unchanged." -ForegroundColor Green
}
else {
Write-Host '  SKIP alternate compiler drift: other VS compiler not installed.' -ForegroundColor Yellow
}

Write-Host "`nTesting refusal to retarget existing build tree..." -ForegroundColor Cyan
$before = (Get-FileHash -LiteralPath $cache -Algorithm SHA256).Hash
$driftManifest = Join-Path $workDir 'workflow-generator-drift.json'
Save-Workflow $driftManifest 'Ninja Multi-Config' $sourceDir $buildDir $binding
$driftResults = Join-Path $workDir 'results-drift'
& $dotnet $cliDll workflow run $driftManifest --results-dir $driftResults --json --no-history
$driftExit = $LASTEXITCODE
if ($driftExit -eq 0) {
throw 'DANGEROUS: generator drift was accepted.'
}
$after = (Get-FileHash -LiteralPath $cache -Algorithm SHA256).Hash
if ($before -ne $after) {
throw 'DANGEROUS: CMakeCache.txt changed during refused drift test.'
}
Write-Host "  PASS mismatched generator refused (exit $driftExit); CMakeCache.txt unchanged." -ForegroundColor Green
}

Write-Host "`nALL CHECKS PASSED" -ForegroundColor Green
Write-Host "Artifacts: $workDir"
Write-Host "Report:    $($reports[0].FullName)"
