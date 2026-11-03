# Windows toolchain discovery and resolution (first implementation slice)

The Host exposes three capability-negotiated, **read-only** methods:

- `toolchains/discover` — request `{}`. Returns `schemaVersion: 1`, platform, Visual Studio instances, candidate
  executable/SDK paths, stable candidate IDs and diagnostics.
- `toolchains/resolve` — request `{ "selection": { "generator": "Ninja", "compilerCandidateId": "..." } }`. CMake,
  Ninja, compiler and SDK candidate IDs are optional. Explicit IDs must be drawn from a new discovery result; unknown
  IDs fail rather than being interpreted as paths. Returns `status: resolved | incomplete | unsupported`, selected
  candidates, developer environment profile ID and diagnostics.
- `toolchains/inspectEnvironment` — request
  `{ "environment": { "environmentProfileId": "vs:<instance-id>", "hostArchitecture": "x64", "targetArchitecture": "x64" } }`.
  Resolves the instance afresh, runs **its** `Common7/Tools/VsDevCmd.bat` in an isolated child `cmd.exe`, and returns a
  fingerprint and a **restricted allowlist** of build environment variables. No inherited secrets are returned. This
  call never alters the Host process environment.

C# SDK: `client.Toolchains.DiscoverAsync()`, `ResolveAsync(selection)`, `InspectEnvironmentAsync(request)`. The Host
advertises the methods in `initialize.capabilities`.

Discovery covers all installed Visual Studio/Build Tools instances through `vswhere -products *`, VS-bundled
CMake/Ninja/MSBuild and MSVC/LLVM, Windows SDKs, `PATH` tools (including package-manager shims), and common standalone
LLVM/CMake installations. A package manager is a *source of installations*, not a prerequisite for detecting an
executable. There is no priority ordering that makes Visual Studio override an explicit standalone selection. Without
explicit IDs, CMake/Ninja may prefer PATH, but **compiler** selection prefers a known MSVC or LLVM/Clang developer
environment over PATH order. MinGW/GNU is not automatically discovered or chosen on Windows; it would require a separate
explicit fallback workflow. Windows doctor probes `cl`, `clang-cl`, `clang++`, not `c++`/`g++`. Linux GCC defaults are
unchanged.

## Safety and limitations

- Discovery is observational: paths and basic metadata are **not compiler/version/ABI validation**. `resolved` means a
  structurally compatible selection exists, not that a given CMake target will compile. Existing CMake cache/generator
  settings and the corresponding toolchain's actual ABI must still be checked at execution admission.
- MSVC and clang-cl selections require a valid matching Visual Studio developer environment profile **and** a discovered
  Windows SDK. A `cl.exe` found on PATH inside one Visual Studio installation cannot be combined with the profile of a
  different instance.
- `inspectEnvironment` remains a **read-only preview**. Execution uses a separate opt-in `WorkflowContext.toolchain`
  binding, resolved afresh by the Host/CLI before dispatch. Preview output is not blindly reused as a credential-bearing
  process environment.
- No registry writes, package installation, `SetEnvironmentVariable`, SDK installation, or elevation occurs in this
  slice. Existing `tooling/doctor`, `tooling/planFix`, `environment/plan` and `environment/apply` retain their current
  semantics. No default Visual Studio installation is silently selected for a previously configured CMake build tree.
- The visible environment projection includes build paths such as `PATH`, `INCLUDE`, `LIB` and `LIBPATH`, which may be
  user-specific. It intentionally omits all non-allowlisted inherited variables. Do not log it by default.
- Other platforms respond with an `unsupported` inventory/resolution and a stable `unsupported_platform` refusal for
  developer-prompt inspection.

See **[Toolchain execution binding](toolchain-execution.md)** for the opt-in workflow execution contract, checkpoint
identity, current limitations and usage.
