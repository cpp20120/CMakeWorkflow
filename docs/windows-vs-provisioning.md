# Windows Visual Studio provisioning (first slice)

`tooling vs` is an explicit Windows-only Visual Studio component installer, independent of
ordinary CMakeWorkflow execution. It uses Microsoft's `vswhere.exe` (Setup Configuration API)
for read-only component discovery and the official Visual Studio Installer for modifications.
It **never** runs as part of a workflow or extension activation.

Profiles:

| Profile | Components |
| --- | --- |
| `windows-cpp` | Native Desktop (or VCTools for Build Tools), latest MSVC x64/x86, Windows 11 SDK 26100, CMake integration, MSVC ASan |
| `windows-cpp-clang` | Visual Studio LLVM/Clang and Clang MSBuild toolset |
| `windows-cpp-perf` | C++ profiling, Build Insights, Windows Performance Toolkit (WPR + WPA) |
| `windows-cpp-graphics` | Visual Studio DirectX graphics tools (not a Vulkan debugger) |

## Existing Visual Studio installation

From an **elevated Windows console** (PowerShell or Windows Terminal):

```powershell
CMakeWorkflow tooling vs plan --profile windows-cpp --profile windows-cpp-perf --json
```

Review the `missingComponents`, `installerPath`, `arguments`, and `approvalSha256`.
For multiple VS instances pass `--instance INSTANCE_ID` or `--install-path PATH` on **both** commands.

```powershell
CMakeWorkflow tooling vs setup --profile windows-cpp --profile windows-cpp-perf --yes --plan-sha APPROVAL_SHA256
```

The installer is invoked with `modify --installPath ... --add ... --passive --norestart`.
The request is re-planned before mutation; a changed plan is rejected. After execution,
component detection is repeated. A nonzero result or missing components means the environment
is **not** verified. Exit code 3010 indicates that a reboot may be needed.

## Fresh machine / no Visual Studio installed

Download the edition-specific official Microsoft bootstrapper first. This slice intentionally
**does not download and execute installers from an arbitrary URL**. Pass the full path to the
bootstrapper and the desired full installation path:

```powershell
CMakeWorkflow tooling vs plan --profile windows-cpp --bootstrapper C:\Downloads\vs_community.exe --install-path C:\VS\Community --json
CMakeWorkflow tooling vs setup --profile windows-cpp --bootstrapper C:\Downloads\vs_community.exe --install-path C:\VS\Community --yes --plan-sha APPROVAL_SHA256
```

For a Build Tools bootstrapper (`vs_buildtools.exe`), use `--build-tools` so that the
`Microsoft.VisualStudio.Workload.VCTools` workload is selected instead of the IDE workload.
The bootstrapper receives `--wait`; `setup.exe modify` does not. No implicit edition selection
or installer download is performed.

## WinDbg and WPA

WPA/WPR are **opt-in** with `windows-cpp-perf` because ETW tracing and scheduler analysis are
particularly useful for concurrent native runtimes. For debugger-specific crash analysis and
Time Travel Debugging, install modern WinDbg separately when needed:

```powershell
winget install --id Microsoft.WinDbg --exact --accept-package-agreements --accept-source-agreements
```

WinDbg is not a prerequisite to compile or debug an ordinary C++ application in Visual Studio.

## Boundaries and follow-up

* `tooling vs` is CLI-only in this slice. Existing `tooling/planFix` / `tooling/applyFix` RPC and
  VS Code GUI do not yet expose the new Visual Studio component installer.
* Reboot and installation interruption are **not** rollback or durability; run `doctor` again
  after recovery. Setup binaries and package versions are not pinned by the approval digest.
* The approval hash guards against changed logical component and instance state. It does **not**
  authenticate the binary or protect against concurrent installer activity.
* Windows user permissions/UAC and security policy can prevent silent or passive installation.
  This command does not auto-elevate or collect credentials.
* Readiness is component presence, not end-to-end compiler usability. Use the existing
  `toolchains/resolve`, `toolchains/inspectEnvironment` and Windows toolchain smoke test to
  validate compiler/linker/SDK compatibility.
* vcpkg manifest installs, Scoop, Vulkan SDK, RenderDoc and binary cache remain separate concerns.
* Unit tests use a fake installer across operating systems; an end-to-end **clean Windows machine**
  installation test is still needed before treating this feature as production-ready.

Official documentation:
[VS command-line installer](https://learn.microsoft.com/en-us/visualstudio/install/use-command-line-parameters-to-install-visual-studio),
[VS component IDs](https://learn.microsoft.com/en-us/visualstudio/install/workload-component-id-vs-community),
[WPT](https://learn.microsoft.com/en-us/windows-hardware/test/wpt),
[WinDbg](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger).
