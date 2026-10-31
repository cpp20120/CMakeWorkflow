[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Cli,
    [Parameter(Mandatory)][string] $Consumer,
    [string] $LogDirectory = 'out/smoke-logs',
    [switch] $InFlight
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$cliPath = (Resolve-Path -LiteralPath $Cli).Path
$consumerPath = (Resolve-Path -LiteralPath $Consumer).Path
$logs = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($LogDirectory)).FullName
# macOS TMPDIR may exceed the Unix socket path limit. Use a short private path.
$temporaryRoot = if ($IsWindows) { [IO.Path]::GetTempPath() } else { '/tmp' }
$directory = Join-Path $temporaryRoot ('cmwf-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
[IO.Directory]::CreateDirectory($directory) | Out-Null
if (-not $IsWindows) {
    [IO.File]::SetUnixFileMode($directory, [IO.UnixFileMode]::UserRead -bor
        [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
}
$endpoint = if ($IsWindows) { '\\.\pipe\cmakeworkflow-' + [Guid]::NewGuid().ToString('N') }
    else { Join-Path $directory 'rpc.sock' }
$workspace = [IO.Directory]::CreateDirectory((Join-Path $directory 'workspace with spaces')).FullName
$server = $null
$stdout = $null
$stderr = $null

function Invoke-CheckedCli([string[]] $Arguments) {
    & $cliPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "CLI $($Arguments -join ' ') exited with $LASTEXITCODE" }
}

try {
    # ArgumentList preserves Windows executable/argument paths with spaces.
    $start = [Diagnostics.ProcessStartInfo]::new($cliPath)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($InFlight) {
        $gateRoot = [IO.Directory]::CreateDirectory((Join-Path $workspace 'rpc-inflight')).FullName
        $start.Environment['CMAKEWORKFLOW_RPC_TEST_ROOT'] = $gateRoot
    } else {
        $start.Environment.Remove('CMAKEWORKFLOW_RPC_TEST_ROOT') | Out-Null
    }
    foreach ($argument in @('host', 'serve', '--endpoint', $endpoint)) { $start.ArgumentList.Add($argument) }
    $server = [Diagnostics.Process]::Start($start)
    $stdout = $server.StandardOutput.ReadToEndAsync()
    $stderr = $server.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($server.HasExited) { throw "Host exited before readiness: $($server.ExitCode)" }
        if ($IsWindows) {
            $pipeName = $endpoint.Substring('\\.\pipe\'.Length)
            $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut)
            try { $pipe.Connect(100); $ready = $true }
            catch [TimeoutException] { }
            finally { $pipe.Dispose() }
        } else { $ready = Test-Path -LiteralPath $endpoint }
        if ($ready) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw 'Host endpoint readiness timed out.' }

    Invoke-CheckedCli @('host', 'doctor', '--endpoint', $endpoint, '--workspace', $workspace, '--profile', 'cmake', '--json')
    Invoke-CheckedCli @('host', 'fix', '--endpoint', $endpoint, '--workspace', $workspace, '--profile', 'cmake', '--pm', 'none', '--no-fallback', '--dry-run', '--json')
    Invoke-CheckedCli @('host', 'fix', '--endpoint', $endpoint, '--workspace', $workspace, '--profile', 'cmake', '--pm', 'none', '--no-fallback', '--yes', '--json')
    $consumerArguments = @($endpoint, $workspace)
    if ($InFlight) { $consumerArguments += '--inflight' }
    & $consumerPath @consumerArguments 2>&1 | Tee-Object (Join-Path $logs 'sdk-consumer.log')
    if ($LASTEXITCODE -ne 0) { throw "NativeAOT consumer exited with $LASTEXITCODE" }
    if ($server.HasExited) { throw 'Consumer stopped the externally owned Host.' }
} finally {
    if ($null -ne $server) {
        try {
            if (-not $server.HasExited) { $server.Kill($true) }
            if (-not $server.WaitForExit(10000)) { throw 'Smoke Host did not stop.' }
            if ($null -ne $stdout) { [IO.File]::WriteAllText((Join-Path $logs 'host.stdout.log'), $stdout.GetAwaiter().GetResult()) }
            if ($null -ne $stderr) { [IO.File]::WriteAllText((Join-Path $logs 'host.stderr.log'), $stderr.GetAwaiter().GetResult()) }
        } finally { $server.Dispose() }
    }
    if ($InFlight -and (Test-Path -LiteralPath (Join-Path $workspace 'rpc-inflight'))) {
        Copy-Item -LiteralPath (Join-Path $workspace 'rpc-inflight') -Destination $logs -Recurse -Force
    }
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
}
