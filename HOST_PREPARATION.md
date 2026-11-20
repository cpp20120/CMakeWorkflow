# Host preparation

The `env doctor|plan|setup` pipeline uses existing Tooling profile discovery, package-manager installation, SHA-256
approval, revalidation and JSONL audit. Host settings are never modified by `plan`, `doctor` or a workflow run.

`env setup FILE --yes --plan-sha SHA --allow-system-changes` permits the explicitly listed settings, and does not
elevate privileges or silently run shell commands. Settings are nontransactional; a failure may leave partial changes,
recorded in the audit log.

Requirements (`schemaVersion: 1`, `toolProfiles: [...]`) may include:

* Linux: `maxPerfEventParanoid` and `cpuGovernor`. Governor changes target
  `/sys/devices/system/cpu/cpufreq/policy*/scaling_governor`.
* Windows: `windowsPowerScheme`, a canonical GUID of an **existing** power plan. For example,
  `381b4222-f694-41f0-9685-ff5bb260df2e` is the standard Balanced GUID, but availability of other schemes varies by
  machine. Windows uses `PowerGetActiveScheme`/`PowerSetActiveScheme` directly, not localized `powercfg` output. This
  setting is shared and may affect battery, thermals and other workloads. There is no automatic rollback.

Windows MSVC/SDK and WPR availability is handled by the existing Tooling profiles; this initial Windows host-settings
backend does not install Windows features or change ETW security policy. Linux-only and Windows-only settings cannot be
mixed. Tests use a fake backend and must also be run on a Windows CI runner; real power-plan mutation is deliberately
not part of automated testing.

## Windows development host checks

Set `requireWindowsMsvc`, `requireWindowsSdk`, and/or `requireWindowsWpr` to `true` in the environment requirements
JSON. These checks discover an installed MSVC x64 compiler using `vswhere` plus `cl.exe`, a Windows SDK with `rc.exe`/
`mt.exe`, and `wpr.exe`. They are observational; installing Visual Studio Build Tools or SDK components is not
automated.
`windows_etw_permissions` is deliberately `unverified`: the presence of WPR does **not** imply that a selected ETW
provider or kernel session can be traced. A WPR requirement therefore cannot be marked fully ready without a separate
authorized trace capability check.

## Pinned Linux tooling

The Linux x86-64 managed tooling path provisions CMake 4.1.2 (including CTest and CPack) and Ninja 1.13.1.
These release binaries use the host glibc ABI. Ninja also needs the host GCC runtime and libstdc++
with GLIBCXX_3.4.21. Installation runs version and functional probes before publication. Other architectures
and operating systems receive an unsupported-platform diagnostic. The existing tooling path is selected when
`--tool-policy` is omitted.

```sh
CMakeWorkflow tooling doctor --tool-policy ManagedOnly
CMakeWorkflow tooling plan --tool-policy ManagedOnly --json
CMakeWorkflow tooling setup --tool-policy ManagedOnly --yes --plan-sha SHA_FROM_PLAN
```

The default managed profile is `cmake-ninja`. `ManagedOnly` verifies the installed release tree against the catalog
before selecting it. `AllowSystem` checks PATH tools first and falls back to a pinned release when one is available.
System tools are identified separately and their version probes establish compatibility only. The `native` profile
adds a compiler check. A managed-only native check reports its compiler/SDK requirement as unresolved.

The Host uses the same policy and exact release selections. Review with `--dry-run`, then use `--yes` to authorize the
current plan. For review across separate calls, SDK clients use `PlanFixAsync` and `ApplyFixAsync` on one connection.
The plan expires after ten minutes and can be applied once. Apply revalidates the planned inputs and checks readiness
after the attempt.

```sh
CMakeWorkflow host doctor --endpoint SOCKET --tool-policy ManagedOnly
CMakeWorkflow host fix --endpoint SOCKET --tool-policy ManagedOnly --dry-run --json
CMakeWorkflow host fix --endpoint SOCKET --tool-policy ManagedOnly --yes
```

The Host advertises `tooling/linuxManaged` on supported platforms. Its doctor and plan requests accept
`resolutionPolicy` (`ManagedOnly` or `AllowSystem`). Plans expose the selected version, URL, archive SHA-256 and
installed-tree SHA-256. The managed path uses no system package manager or elevation. `--pm none` is accepted.
Set `CMAKEWORKFLOW_TOOL_STORE` on the CLI or Host process to configure the absolute store path. The default is
`~/.local/share/CMakeWorkflow`. Use a private location below the home directory or a private test directory under
`/tmp`.

Published releases live under `toolchains/<tool>/<version>/linux-x64/<archive-sha256>`. Downloads and extraction
run under a per-artifact lock and use staging on the store filesystem. An interrupted attempt can be retried.
Installations are retained side by side. There is no automatic garbage collection. To reclaim space, stop all
CLI and Host processes using the store and remove only obsolete digest directories without live workflows,
build-tree bindings or retained checkpoints that need them. Leave the lock files in place. The project artifact
CAS has a separate lifecycle and does not own user-wide installations.

### Activating tools in a workflow

Use an explicit toolchain binding on a managed build context. This example selects verified managed CMake, CTest
and Ninja together with the host's native `cc`, `c++` and SDK. This compiler and SDK selection is external to the
managed store.

```json
{
  "buildDirectory": "build",
  "sourceDirectory": ".",
  "mode": "managed",
  "toolchain": {
    "environmentProfileId": "linux-managed-tools-system-compiler",
    "generator": "Ninja"
  }
}
```

Use `linux-allow-system-tools` to use the `AllowSystem` resolution policy at workflow admission. Both profiles
require native x86-64 compilers and support Ninja and Ninja Multi-Config. Optional `cmakeCandidateId` and
`ninjaCandidateId` values of `sha256:<archive-sha256>` require the selected tool to match that managed artifact.
The child environment and exact executable paths apply to configure, build, and test. The build tree stores the
toolchain fingerprint, and resume checks it against the saved selection. Keep the original paths for an existing
build tree or checkpoint. A relocated store can be used with a new build tree.

Managed LLVM/LLD, verified sysroots, pinned vcpkg bootstrap, and reusable environment lock manifests are outside this
slice. The host SDK and system runtime libraries remain part of the build environment. Release pins are from
[Kitware CMake 4.1.2](https://github.com/Kitware/CMake/releases/tag/v4.1.2) and
[Ninja 1.13.1](https://github.com/ninja-build/ninja/releases/tag/v1.13.1).

To exercise a real installation and native C++ configure/build/test, run
`tests/infrastructure/managed-tooling/run-smoke.sh PATH_TO_CMAKEWORKFLOW`. This opt-in check downloads releases
from GitHub into a disposable store. The regular Tooling tests use fake release data and HTTP responses.
