# Project SDK v1.1 (P1: resolution and policy)

The Project SDK owns conventions, not compilation. It emits a standalone CMake
project and vendors versioned modules under `cmake/cmwf-sdk/v1`. CMake owns the
native target graph; the existing workflow engine owns configure/build/test
orchestration. Nothing is downloaded or installed by `project new`.

## Create and build

```sh
CMakeWorkflow project new library SampleLib ./SampleLib
CMakeWorkflow project new application SampleApp ./SampleApp
cd SampleLib
cmake --workflow --preset release
CMakeWorkflow workflow run workflow.json
```

The destination must be new and its parent must exist. The library template
exports a namespaced CMake target, public headers and an exact-version package
config. The application template installs its executable. Both have Debug and
Release presets, CTest smoke tests and CPack TGZ packaging. The SDK applies a
C++23 default to its own targets. For new P1 projects the SDK policy is
read from a committed lock, not from a cache override.
Normal CMake target-local settings and additional targets remain available.
Public external dependencies need explicit dependency discovery in the installed
package config; the starter does not infer them. Shared Windows libraries with
more complex interfaces should use explicit export annotations.

The ambient workflow uses the Debug CMake preset. The release workflow preset
also packages the product; CPack is not yet a separate CMakeWorkflow DAG node.
## Resolve and lock SDK policy

New projects contain `cmwf.project.json`, `cmwf.sdk.lock.json` and the vendored
SDK catalog and modules. The CLI currently resolves only built-in SDK version
`1.1.0` (no version ranges, network registry, plugins or automatic upgrade).
The catalog defines `Native.Base` (abstract) and concrete `Native.Library` and
`Native.Application`, with an explicit inheritance chain. Selection does not
rewrite a project's native target graph. Project templates and SDK type should
be kept consistent by the project author.

```json
{
  "schemaVersion": 1,
  "sdk": "Native.Library",
  "sdkVersion": "1.1.0",
  "policyFiles": ["policies/team.json", "policies/project.json"],
  "settings": { "cxxStandard": 20 }
}
```

The policy files are JSON objects (e.g. `{"warningsAsErrors": true}`).
Evaluation is base SDK → concrete SDK → listed files in order → `settings`.
A later explicit property overrides an earlier property, including `false`.
Supported values: `cxxStandard` (17, 20, 23), `cxxExtensions` (boolean), and
`warningsAsErrors` (boolean). Unknown and duplicate fields are rejected.
Policy file paths are project-relative, explicit, portable, and must not traverse
outside the project or through symbolic links. There is no implicit user/home
policy, no recursive discovery and no cross-project policy merge. To apply a
local override, explicitly list the file or set a property in `settings`;
commit all lock inputs for a portable checkout. Files should use LF, as recorded
in the generated `.gitattributes`.

```sh
CMakeWorkflow project sdk list
CMakeWorkflow project sdk plan .
CMakeWorkflow project sdk lock . --plan-sha SDK_PLAN_SHA256
CMakeWorkflow project sdk verify .
cmake --workflow --preset release
```

`plan` is read-only, emits the canonical proposed lock JSON to stdout and its
SHA-256 to stderr. `lock` recomputes the plan, rejects stale approvals and
publishes one file via a same-directory temporary file and rename. `verify`
re-evaluates the project and checks exact lock bytes. The lock records SDK
lineage, effective policy, and hashes of every manifest/policy input plus
vendored SDK payload. Its identity does not include the absolute checkout path.
SDK resources are normalized to UTF-8/LF at materialization. Policy files are
hashed as stored, not semantically normalized. Plan output has a trailing
console newline; use the reported digest rather than hashing stdout.

CMake reads the checked-in lock and checks input/module hashes on configure;
`CMAKE_CONFIGURE_DEPENDS` makes input changes trigger regeneration. It consumes
locked target defaults without the CLI. This is consistency checking for trusted
source code, not cryptographic authentication of an independently edited lock
or a sandbox. Use `project sdk verify` in CI to detect manual lock modifications.
Target-local CMake overrides remain possible; the lock describes SDK defaults,
not all target properties or full build hermeticity. Keep project inputs stable
while locking/building; lock publication is not a transaction over other files.

P0 projects already generated with the older vendored module remain standalone
and unchanged. P1 does not silently migrate their existing files. For now,
generate a fresh P1 project and explicitly port project-specific sources and
CMake declarations; an in-place SDK upgrade/adoption command is future work.
The managed environment lock and vcpkg receipt remain separate existing
contracts; SDK resolution neither provisions tools nor installs dependencies.
See [project schema](../../schemas/project-sdk/project.v1.schema.json) and
[policy schema](../../schemas/project-sdk/policy.v1.schema.json).

## Bind a managed toolchain (Linux x86-64)

See [Toolchain management](toolchain-management.md) for v2 locks,
`settings.toolchain` and `project sdk workflow`. The following uses the
original v1 lock workflow.

```sh
CMakeWorkflow project tooling plan
CMakeWorkflow project tooling setup --yes --plan-sha PLAN_SHA256
CMakeWorkflow project tooling doctor
CMakeWorkflow tooling lock --write
CMakeWorkflow project new library ManagedLib ./ManagedLib --environment-lock ENVIRONMENT_LOCK_SHA256
CMakeWorkflow workflow plan ManagedLib/workflow.managed.json
CMakeWorkflow workflow run ManagedLib/workflow.managed.json
```

`project tooling` delegates to existing Tooling with `linux-clang` plus
`packaging` and `ManagedOnly`. It uses the existing plan approval, checksummed
releases, side-by-side store, and host admission. It never falls back to the
system package manager. Copy the digest printed by `tooling lock --write`, not
the setup plan digest. The optional `--environment-lock` generates a separate
workflow with a pinned environment identity; the ambient workflow remains
unchanged. The managed workflow deliberately uses a separate build directory
and no configure preset: the existing provider rejects mixing an explicit
binding with a configure preset. It resolves tools on the execution machine.
Provision the same pinned environment and lock there before executing remotely.

Managed execution is still Linux x86-64 only and inherits the existing runtime
ABI, filesystem and toolchain limitations. CPack is included in the verified
CMake distribution; `packaging` additionally exposes it to the tooling plan.
The lock is the existing toolchain identity, not a claim of complete build
hermeticity. vcpkg remains an explicitly planned/approved separate dependency
operation. This change does not add a new tool store, registry or resolver.

## Next slices

1. Add adoption and upgrade plans, additional installed SDK versions, recipe
   composition and additional toolchain profiles.
2. Expand the SDK catalog to runtime/Qt/interop and add target-scoped quality
   policies and separate compiler/platform configuration axes.
3. Generalize managed release catalogs, inventory and lock lifecycle; add
   platform providers incrementally and validate compile/link/run admission.
4. Generate package and benchmark workflows through the existing engine rather
   than bypassing it with implicit execution.

Run the smoke test with `bash tests/infrastructure/project-sdk/run-tests.sh`
followed by the path to a built CLI (or `dotnet /absolute/path/CMakeWorkflow.dll`).
It exercises scaffold, no-overwrite, native presets, packaging, workflow,
and consumption of the installed library. Managed provisioning requires a
separate real Linux x64 integration test.

Run `bash tests/infrastructure/project-sdk/run-p1-tests.sh` with the built CLI
for lock approval, inheritance, relocation, stale inputs, tampering and native
policy application. Run it with both ordinary and NativeAOT CLI builds.
