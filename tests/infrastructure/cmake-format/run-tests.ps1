param([Parameter(Mandatory)][string] $Cli)

$ErrorActionPreference = 'Stop'
$cliPath = (Resolve-Path $Cli).Path
$temp = if ($IsWindows) { [IO.Path]::GetTempPath() } else { '/tmp' }
$root = Join-Path $temp ('cmwf-format-' + [Guid]::NewGuid().ToString('N'))
$previousStore = $env:CMAKEWORKFLOW_TOOL_STORE
$env:CMAKEWORKFLOW_TOOL_STORE = Join-Path $root 'store'
$project = Join-Path $root 'project ü'
$null = New-Item -ItemType Directory (Join-Path $project 'modules/build'), (Join-Path $project 'generated'), (Join-Path $project 'nested') -Force
$policy = @('--tool-policy', 'ManagedOnly')
$format = @('format', '--project', $project, '--language', 'cmake', '--json') + $policy

function Run-Json([int] $Expected, [string[]] $Arguments) {
    $output = & $cliPath @Arguments 2> (Join-Path $root 'stderr')
    if ($LASTEXITCODE -ne $Expected) {
        throw "Expected $Expected, got $LASTEXITCODE for $Arguments. $output $(Get-Content (Join-Path $root 'stderr') -Raw)"
    }
    $output | ConvertFrom-Json
}
function Write-Source([string] $Path, [string] $Content) {
    [IO.File]::WriteAllText((Join-Path $project $Path), $Content)
}
function Find-Status($Report, [string] $Path) {
    ($Report.files | Where-Object path -CEQ $Path).status
}
try {
    $planArgs = @('--profile', 'cmakefmt', '--json') + $policy
    Write-Source CMakeLists.txt "project(FormatFixture NONE)`n"
    $missing = Run-Json 2 ($format + '--check')
    if ($missing.invocations[0].error -notmatch 'cmakefmt is unavailable') { throw 'Missing formatter was accepted' }
    $absent = Run-Json 2 (@('tooling', 'doctor') + $planArgs)
    if ($absent.tools[0].resolution -ne 'downloadable') { throw 'No pinned release detected' }
    $plan = Run-Json 0 (@('tooling', 'plan') + $planArgs)
    $planLog = Get-Content (Join-Path $root 'stderr') -Raw
    if ($plan.steps.Count -ne 1 -or $plan.steps[0].release.version -ne '2.4.0' -or
        $planLog -notmatch 'Plan SHA-256: ([a-f0-9]{64})') { throw 'Invalid formatter plan' }
    $sha = $Matches[1]
    $null = Run-Json 0 (@('tooling', 'setup', '--yes', '--plan-sha', $sha) + $planArgs)
    $ready = Run-Json 0 (@('tooling', 'doctor') + $planArgs)
    $tool = $ready.tools[0].command

    Write-Source CMakeLists.txt "cmake_minimum_required(VERSION 3.26)`nproject(FormatFixture NONE)`ninclude(`"`${CMAKE_CURRENT_LIST_DIR}/modules/build/espaço one.cmake`")`ninclude(nested/two.cmake)`n"
    Write-Source 'modules/build/espaço one.cmake' "set( FIRST value )`n"
    Write-Source 'nested/two.cmake' "SET( SECOND value )`n"
    Write-Source 'nested/.cmakefmt.toml' "[format]`ncommand_case = 'upper'`n"
    Write-Source 'generated/CMakeCache.txt' "# fixture`n"
    Write-Source 'generated/bad.cmake' "set(`n"
    Write-Source 'ignored.cmake' "set(`n"
    Write-Source '.cmakefmtignore' "ignored.cmake`n"
    Write-Source '.gitignore' "/hidden.cmake`n"
    Write-Source 'hidden.cmake' "set(`n"
    git -C $project init --quiet

    $before = Get-FileHash (Join-Path $project 'modules/build/espaço one.cmake')
    $check = Run-Json 1 ($format + '--check')
    if ($check.status -ne 'FormattingRequired' -or $check.invocations.Count -ne 2 -or
        (Find-Status $check 'modules/build/espaço one.cmake') -ne 'FormattingRequired' -or
        (Find-Status $check 'ignored.cmake') -ne 'Skipped' -or (Find-Status $check 'hidden.cmake') -ne 'Skipped' -or
        (Find-Status $check 'generated/bad.cmake') -or $before.Hash -ne (Get-FileHash $before.Path).Hash) {
        throw 'Invalid formatter file selection or check mutation'
    }
    $diff = Run-Json 1 ($format + @('--diff', '--file', 'modules/build/espaço one.cmake', '--file', './modules/build/espaço one.cmake'))
    if ($diff.files.Count -ne 1 -or $diff.files[0].diff -notmatch '\+set\(FIRST value\)') { throw 'Missing diff' }
    $ignored = Run-Json 0 ($format + @('--file', 'ignored.cmake'))
    if ($ignored.files[0].status -ne 'Skipped' -or $ignored.invocations.Count -ne 1) { throw 'Ignore bypass' }

    Write-Source 'invalid.cmake' "set(`n"
    $partial = Run-Json 2 ($format + '--apply')
    if ((Find-Status $partial 'invalid.cmake') -ne 'Failed' -or
        (Find-Status $partial 'modules/build/espaço one.cmake') -ne 'Formatted') { throw 'Invalid partial apply report' }
    Remove-Item (Join-Path $project 'invalid.cmake')
    $null = Run-Json 0 ($format + '--check')
    $again = Run-Json 0 ($format + '--apply')
    if ($again.status -ne 'Clean' -or
        (Get-Content (Join-Path $project 'nested/two.cmake') -Raw) -cnotmatch '^SET\(SECOND value\)') {
        throw 'Nested configuration or idempotence failed'
    }

    Write-Source '.editorconfig' "root = true`n[*]`nindent_style = space`nindent_size = 4`n"
    Write-Source 'editor.cmake' "if(TRUE)`nmessage(STATUS ready)`nendif()`n"
    $previousHome = $env:HOME
    $fakeHome = Join-Path $root 'ambient-home'
    $null = New-Item -ItemType Directory -Path $fakeHome
    [IO.File]::WriteAllText((Join-Path $fakeHome '.cmakefmt.toml'), "[format]`ncommand_case = 'upper'`n")
    try {
        $env:HOME = $fakeHome
        $null = Run-Json 0 ($format + @('--apply', '--file', 'editor.cmake'))
    }
    finally {
        $env:HOME = $previousHome
    }
    if ((Get-Content (Join-Path $project 'editor.cmake') -Raw) -cnotmatch '(?m)^    message\(') {
        throw 'EditorConfig fallback or ambient home isolation failed'
    }

    Write-Source 'nested/.cmakefmt.toml' "[format`n"
    $invalidConfig = Run-Json 2 ($format + @('--file', 'nested/two.cmake'))
    if ((Find-Status $invalidConfig 'nested/two.cmake') -ne 'Unprocessed' -or
        $invalidConfig.invocations[1].error -notmatch 'invalid config file') { throw 'Invalid config diagnostic was missing' }
    Write-Source 'nested/.cmakefmt.toml' "[format]`ncommand_case = 'upper'`n"
    if ($IsLinux -and (Get-Command clang-format -ErrorAction SilentlyContinue)) {
        Write-Source 'fixture.cpp' "int   value;`n"
        $mixed = Run-Json 1 @('format', '--project', $project, '--check', '--json')
        if ($mixed.tools.Count -ne 2 -or (Find-Status $mixed 'fixture.cpp') -ne 'FormattingRequired' -or
            (Find-Status $mixed 'nested/two.cmake') -ne 'Clean') { throw 'Mixed formatter session failed' }
        Remove-Item (Join-Path $project 'fixture.cpp')
        $null = New-Item -ItemType SymbolicLink -Path (Join-Path $project 'linked.cmake') -Target (Join-Path $project 'nested/two.cmake')
        $linked = Run-Json 2 ($format + @('--file', 'linked.cmake'))
        if ($linked.invocations[0].error -notmatch 'link') { throw 'External link accepted' }
        Remove-Item (Join-Path $project 'linked.cmake')
    }

    git -C $project add .
    git -C $project -c user.name=Smoke -c user.email=smoke@example.com commit --quiet -m fixture
    Write-Source 'nested/two.cmake' "SET( SECOND value )`n"
    $changed = Run-Json 1 ($format + '--changed')
    if ($changed.files.Count -ne 1) { throw 'Unexpected changed scope' }
    git -C $project add 'nested/two.cmake'
    $staged = Run-Json 1 ($format + '--staged')
    if ($staged.files[0].path -ne 'nested/two.cmake') { throw 'Unexpected staged scope' }
    Write-Source 'nested/two.cmake' "SET(SECOND value)`n"
    $null = Run-Json 2 ($format + '--staged')

    $manifest = Join-Path $project 'workflow.json'
    $definition = @{
        schemaVersion = 2
        contexts = @{ native = @{ sourceDirectory = '.'; buildDirectory = 'build-tree' } }
        nodes = @(
            @{ id = 'cmake-style'; kind = 'format_check'; context = 'native'; arguments = @('cmake', 'managed') },
            @{ id = 'configure'; kind = 'configure'; context = 'native'; dependsOn = @('cmake-style') }
        )
    }
    $definition | ConvertTo-Json -Depth 8 | Set-Content $manifest
    $null = & $cliPath workflow run $manifest --results-dir (Join-Path $root 'clean-run') --no-history
    if ($LASTEXITCODE -ne 0 -or !(Test-Path (Join-Path $project 'build-tree/CMakeCache.txt'))) { throw 'Clean workflow gate failed' }
    Remove-Item (Join-Path $project 'build-tree') -Recurse -Force
    Write-Source 'nested/two.cmake' "SET( SECOND value )`n"
    $null = & $cliPath workflow run $manifest --results-dir (Join-Path $root 'blocked-run') --no-history
    if ($LASTEXITCODE -eq 0 -or (Test-Path (Join-Path $project 'build-tree/CMakeCache.txt'))) { throw 'Formatting gate allowed configure' }
    $gate = Get-ChildItem (Join-Path $root 'blocked-run') -Recurse -Filter '*.stdout.log' | Select-Object -First 1
    if (($gate | Get-Content -Raw | ConvertFrom-Json).status -ne 'FormattingRequired') { throw 'Missing gate report' }

    [IO.File]::AppendAllText($tool, 'tampered')
    $tampered = Run-Json 2 ($format + '--check')
    if ($tampered.invocations[0].error -notmatch 'SHA-256') { throw 'Tampered formatter accepted' }
    Write-Host 'PASS pinned cmakefmt, check/diff/apply and partial failure, nested config, Git scopes, and workflow gate'
}
finally {
    $env:CMAKEWORKFLOW_TOOL_STORE = $previousStore
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
