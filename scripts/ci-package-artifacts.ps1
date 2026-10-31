[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('linux-x64', 'win-x64', 'osx-arm64')][string] $Rid,
    [string] $NativeDirectory = 'out/native',
    [string] $ConsumerDirectory = 'out/sdk-aot',
    [string] $PackageDirectory = 'out/sdk-packages',
    [string] $OutputDirectory = 'out/distribution'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$output = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)).FullName
$suffix = if ($Rid -eq 'win-x64') { '.exe' } else { '' }
foreach ($binary in @((Join-Path $NativeDirectory "CMakeWorkflow$suffix"),
                     (Join-Path $ConsumerDirectory "CMakeWorkflow.Client.AotSmoke$suffix"))) {
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Missing native binary: $binary" }
}
$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter 'CMakeWorkflow.Client.*.nupkg' -File)
if ($packages.Count -ne 1) { throw 'Expected exactly one audited SDK package.' }

# upload-artifact ZIPs lose Unix executable bits; tar preserves them. Include the
# complete publish outputs, including platform runtime dependencies and symbols.
$nativeArchive = Join-Path $output "CMakeWorkflow-$Rid.tar.gz"
tar -czf $nativeArchive -C $NativeDirectory .
if ($LASTEXITCODE -ne 0) { throw 'CLI archive creation failed.' }
$consumerArchive = Join-Path $output "CMakeWorkflow.Client.AotSmoke-$Rid.tar.gz"
tar -czf $consumerArchive -C $ConsumerDirectory .
if ($LASTEXITCODE -ne 0) { throw 'Consumer archive creation failed.' }
$package = Join-Path $output $packages[0].Name
Copy-Item -LiteralPath $packages[0].FullName -Destination $package
$metadata = Join-Path $output 'build.json'
@{
    rid = $Rid
    commit = $env:GITHUB_SHA
    runId = $env:GITHUB_RUN_ID
    runAttempt = $env:GITHUB_RUN_ATTEMPT
    sdkPackage = $packages[0].Name
} | ConvertTo-Json | Set-Content -LiteralPath $metadata -Encoding utf8
$checksums = foreach ($path in @($nativeArchive, $consumerArchive, $package, $metadata)) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($path))"
}
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS'), ($checksums -join "`n") + "`n")
