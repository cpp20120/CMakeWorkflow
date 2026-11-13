# Tool provisioning, CMake bridge and IDE integration

**[English](integrations.md) · [Русский](../integrations.md) · [Index](README.md)**

All integrations are optional: CMakeWorkflow can operate as a standalone CLI against an existing configured CMake tree.
It does not require the author's boilerplate, a custom CMake module or VS Code.

## Tool provisioning

```sh
CMakeWorkflow tooling list --json
CMakeWorkflow tooling doctor --profile native --json
CMakeWorkflow tooling plan --profile native --profile profiling --json
CMakeWorkflow tooling setup --profile native --dry-run --json
CMakeWorkflow tooling setup --profile native --yes
```

Profiles: `native`, `cmake`, `ninja`, `profiling`, `fuzz`, `development`, `all`. Doctor checks commands and versions;
plan is read-only; setup probes again and mutates only when explicitly authorized with `--yes`. It never runs implicitly
during workflow execution. Package-manager options include apt, pacman, dnf, Homebrew and winget; `--pm none` disables
package installs, `--no-fallback` disables standalone fallback, and `--install-dir` selects a user binary directory.
Linux package setup uses non-interactive `sudo`; neither the CLI nor Host prompts for a password. Cancellation does
**not** roll back partial package installation.

For Linux x64/arm64 CMake/CTest, the standalone fallback requires an official Kitware release with a published and
verified SHA-256 checksum; arbitrary install scripts are not executed. Windows ADK and macOS Xcode installation are
diagnosed but not automated. Tool availability does **not** imply profiler event permissions, a compatible compiler
language feature set or a valid pre-existing CMake cache. A tooling setup never silently rewrites a CMake build tree's
recorded toolchain.

## Optional CMake target bridge

[cmake/CMakeWorkflowTargetBridge.cmake](../../cmake/CMakeWorkflowTargetBridge.cmake) allows a custom target to invoke a
**prepared executable** without recursively building its own controller tree. `WORKFLOW_RUNNER_EXECUTABLE` must point to
the published CLI and native prerequisites must be declared with CMake `DEPENDS`.

`--prepared` is valid only in that declared bridge contract, not a general bypass of build verification. Outside the
bridge, normal target build/run goes through CMake and CMake File API resolution. The bridge is **not required** for
ordinary use.

## Boilerplate, CTest and CDash

[cmake_boilerplate](https://github.com/cpp20120/cmake_boilerplate) is a separate optional project with project
generation, presets, testing, sanitizers, coverage and a CTest/CDash pipeline. CMakeWorkflow does not absorb compiler
flags or native dependency logic from it; each can work without the other. CTest discovers and runs tests; CDash
receives opted-in CTest dashboard submissions; History is a separate local SQLite index of workflow, benchmark and
profiler artifacts, **not a CDash server**.

## VS Code extension

The [VS Code extension](https://github.com/cpp20120/cmake_workflow_vscode) runs on the workspace host, including
Remote SSH, WSL and Dev Containers. Set `cmakeWorkflow.executable` to a native binary or to `dotnet` with the absolute
DLL path as a separate element in `cmakeWorkflow.prefixArgs`.

Workflow planning, full and selected-node runs, History, and saved profile analysis use Host RPC. **Host Operations**
shows operation and node state, History indexing and a separate raw events tab. Its **Open Host API** form exposes
methods advertised by the connected Host. **History & Comparison** shows the selected catalog and available indexing
diagnostics when it is empty. In **… → Profiles**, use **Saved Profiles** to select a run or **Open Existing Profile…**
to browse to a run folder.

The dedicated profile capture and replay, target, tooling setup and DagFlow campaign wizards still invoke standalone
CLI commands. See the [extension README](https://github.com/cpp20120/cmake_workflow_vscode/blob/master/README.md)
for configuration and compatibility details.

## Testing boundaries

```sh
dotnet tests/bin/Release/net10.0/CMakeWorkflow.IntegrationTests.dll bin/Release/net10.0/CMakeWorkflow
dotnet CMakeWorkflow.History.Tests/bin/Release/net10.0/CMakeWorkflow.History.Tests.dll
dotnet CMakeWorkflow.Profiling.Tests/bin/Release/net10.0/CMakeWorkflow.Profiling.Tests.dll
dotnet CMakeWorkflow.Campaigns.Tests/bin/Release/net10.0/CMakeWorkflow.Campaigns.Tests.dll
dotnet CMakeWorkflow.Tooling.Tests/bin/Release/net10.0/CMakeWorkflow.Tooling.Tests.dll
```

On Windows the executable path in the first invocation ends with `.exe`. `.github/workflows/ci.yml` checks a platform
matrix, but the **presence of a CI job** is not proof that the exact checkout passed it: inspect the actual run result.
Parser and mocked tests do not verify PMU rights, complete ETW/Instruments event delivery, package installation from a
pristine machine, hardware power-loss recovery or cross-repository release compatibility. Those require explicit
host/integration acceptance runs.
