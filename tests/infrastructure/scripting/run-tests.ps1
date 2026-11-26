[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Cli,
    [Parameter(Mandatory)][string] $Feed,
    [Parameter(Mandatory)][string] $Version
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$cliPath = (Resolve-Path -LiteralPath $Cli).Path
$feedPath = (Resolve-Path -LiteralPath $Feed).Path
$previousSources = [Environment]::GetEnvironmentVariable('RestoreAdditionalProjectSources')
$root = [IO.Directory]::CreateTempSubdirectory('cmwf-script-test-')
try {
    $workspace = [IO.Directory]::CreateDirectory((Join-Path $root.FullName 'native space ü')).FullName
    $env:RestoreAdditionalProjectSources = $feedPath
    dotnet pack examples/scripting/NativeFragments/NativeFragments.csproj -c Release "-p:ScriptingVersion=$Version" "-p:Version=$Version" -p:TreatWarningsAsErrors=true -o $feedPath
    if ($LASTEXITCODE -ne 0) { throw 'Fragment package failed to build.' }
    Copy-Item examples/scripting/native/* $workspace
    $author = Join-Path $workspace 'workflow.cs'
    (Get-Content -LiteralPath $author -Raw).Replace('@0.1.0-script.1', "@$Version") |
        Set-Content -LiteralPath $author -Encoding utf8
    function Invoke-Case([int] $ExpectedCode, [string] $Pattern, [string[]] $Arguments) {
        $text = (& $cliPath @Arguments 2>&1 | Out-String)
        if ($LASTEXITCODE -ne $ExpectedCode -or $text -notmatch $Pattern) {
            throw "Unexpected result ($LASTEXITCODE) for $($Arguments -join ' '): $text"
        }
        Write-Host "PASS $Pattern"
    }

    Invoke-Case 0 'SHA-256 [a-f0-9]{64}' @('script', 'compose', $author, '--', '--matrix')
    $matrix = Get-ChildItem -LiteralPath $workspace -Filter '.cmwf-workflow-*.json' -Force | Select-Object -First 1
    $nodes = (Get-Content -LiteralPath $matrix.FullName -Raw | ConvertFrom-Json).nodes
    if (@($nodes | Where-Object kind -eq 'run').Count -ne 6 -or
        @($nodes | Where-Object id -eq 'smoke.test').Count -ne 1) {
        throw 'The external fragments did not receive the forwarded matrix argument or share the smoke prerequisite.'
    }

    dotnet run --project examples/scripting/Matrix "-p:ScriptingVersion=$Version" -p:TreatWarningsAsErrors=true -- $workspace
    if ($LASTEXITCODE -ne 0) { throw 'Standalone package consumer failed.' }
    Invoke-Case 1 'approve is required' @('script', 'run', $author)
    $failureCase = Join-Path $workspace 'negative.cs'
    Set-Content -LiteralPath $failureCase -Encoding utf8 -Value @'
switch (args[0])
{
    case "failure": Console.Error.WriteLine("script stderr marker"); return 17;
    case "missing": Console.WriteLine("ordinary script stdout marker"); break;
    case "replace":
        string responsePath = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_SCRIPT_RESPONSE")!;
        File.Delete(responsePath);
        await File.WriteAllTextAsync(responsePath, "{");
        break;
    case "timeout":
        Console.Error.WriteLine($"author child pid {Environment.ProcessId}");
        await Task.Delay(TimeSpan.FromMinutes(5));
        break;
    default:
        string path = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_SCRIPT_RESPONSE")!;
        string response = args[0] switch
        {
            "malformed" => "{",
            "version" => """{"version":2,"relativePaths":"manifest-directory","manifestBytes":"e30=","sha256":""}""",
            "oversized" => new string('x', 1500000),
            _ => """{"version":1,"relativePaths":"manifest-directory","manifestBytes":"e30=","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}"""
        };
        await File.WriteAllTextAsync(path, response);
        break;
}
return 0;
'@
    Invoke-Case 1 'process exited with code 17' @('script', 'compose', $failureCase, '--', 'failure')
    Invoke-Case 1 '(?s)(?=.*ordinary script stdout marker)(?=.*did not write a response)' @('script', 'compose', $failureCase, '--', 'missing')
    Invoke-Case 1 'response version' @('script', 'compose', $failureCase, '--', 'version')
    Invoke-Case 1 'response size' @('script', 'compose', $failureCase, '--', 'oversized')
    Invoke-Case 1 'SHA-256 does not match' @('script', 'compose', $failureCase, '--', 'digest')
    Invoke-Case 1 'Json|property|JSON' @('script', 'compose', $failureCase, '--', 'malformed')
    Invoke-Case 1 'timed out' @('script', 'compose', $failureCase, '--timeout-seconds', '2', '--', 'timeout')
    if ($IsLinux) {
        Invoke-Case 1 'did not write a response' @('script', 'compose', $failureCase, '--', 'replace')
        dotnet tests/bin/Release/net10.0/CMakeWorkflow.IntegrationTests.dll $cliPath --script-cancellation $failureCase
        if ($LASTEXITCODE -ne 0) { throw 'Controller cancellation or child cleanup failed.' }
        $shim = [IO.Directory]::CreateDirectory((Join-Path $root.FullName 'sdk-shim')).FullName
        $previousPath = $env:PATH
        try {
            $env:PATH = $shim
            Invoke-Case 1 'SDK could not be started' @('script', 'compose', $author)
            $fakeSdk = Join-Path $shim 'dotnet'
            Set-Content -LiteralPath $fakeSdk -Value "#!/bin/sh`nprintf '9.0.100\n'"
            [IO.File]::SetUnixFileMode($fakeSdk, [IO.UnixFileMode]::UserRead -bor
                [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
            Invoke-Case 1 'SDK 10 or later' @('script', 'compose', $author)
        } finally {
            $env:PATH = $previousPath
        }
    }
    Set-Content -LiteralPath $failureCase -Value 'invalid C# compilation'
    Invoke-Case 1 'error CS' @('script', 'compose', $failureCase)

    if ($IsLinux) {
        Invoke-Case 0 'smoke.test' @('script', 'plan', $author)
        if (Test-Path (Join-Path $workspace 'build')) { throw 'Preflight created the build directory.' }
        Invoke-Case 1 'absent' @('script', 'run', $author, '--approve', '--only', 'absent')
        if (Test-Path (Join-Path $workspace 'build')) { throw 'Invalid preflight dispatched work.' }
        $database = Join-Path $workspace 'history.sqlite'
        $runArgs = @('script', 'run', $author, '--approve', '--request-id', 'file-based-acceptance', '--history-db', $database)
        Invoke-Case 0 'succeeded' $runArgs
        Invoke-Case 0 'succeeded' $runArgs
        Invoke-Case 1 'request_id_conflict' ($runArgs + @('--jobs', '2'))
        if (-not (Test-Path $database)) { throw 'The Host did not index the requested History database.' }
        $history = Join-Path $workspace '.cmakeworkflow/script-results'
        if (-not (Get-ChildItem -LiteralPath $history -Recurse -Filter 'run.json')) { throw 'Missing durable run report.' }
    }
} finally {
    [Environment]::SetEnvironmentVariable('RestoreAdditionalProjectSources', $previousSources)
    $root.Delete($true)
}

# Expected CLI failures leave LASTEXITCODE nonzero. Return the suite's result.
exit 0
