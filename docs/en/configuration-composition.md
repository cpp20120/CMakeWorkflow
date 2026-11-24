# Project SDK configuration and build variants

[Русский](../ru/configuration-composition.md)

The configuration extension v1 is available to opted-in Library and Application
scaffolds. It uses the existing SDK 1.1.0 package and approved Project SDK lock.
The extension has its own vendored module and versioned configuration lock.

```sh
cmakeworkflow project new library Demo ./Demo --variants
cd Demo
```

Edit `cmwf.config.json` to declare profiles and optionally a matrix.

```json
{
  "schemaVersion": 1,
  "settings": { "testing": true },
  "profiles": {
    "release": { "configuration": "Release", "optimization": "speed", "lto": false }
  },
  "matrix": {
    "profile": "release",
    "axes": { "configuration": ["Debug", "Release"], "linkage": ["static", "shared"] },
    "exclude": [{ "configuration": "Debug", "linkage": "shared" }]
  }
}
```

```sh
cmakeworkflow project config plan . --matrix
cmakeworkflow project config lock . --matrix --plan-sha APPROVED_SHA256
cmakeworkflow project config verify . --matrix
cmakeworkflow project config workflow . --matrix > variants.json
cmakeworkflow workflow run variants.json --jobs 3 --results-dir out/results
cmakeworkflow workflow report out/results/RUN_DIRECTORY --json
```

Repeat the same selector and `--overrides FILE` options in all commands. Use
`--profile release` to plan a single profile, or `workflow --matrix --variant ID`
to emit a subset of an approved matrix. The generated workflow must be saved
at the project root. It has a configure → invoke `all` → CTest branch per
variant. With `testing: false`, the branch ends at build.

The corresponding workflow context has this shape. Managed variants also
carry the toolchain binding from the SDK lock.

```json
{
  "sourceDirectory": ".",
  "buildDirectory": "out/build/Release-<variant-sha256>",
  "configuration": "Release",
  "generator": "Ninja",
  "mode": "managed",
  "buildVariant": { "id": "<variant-sha256>", "lockSha256": "<configuration-lock-sha256>" }
}
```

## Ownership and composition

- Project SDK resolves the project type, language policy and environment
  reference. Its lock and vendored 1.1.0 modules keep their existing identity.
- Toolchain Management owns compiler, linker, SDK, target ABI and host-local
  fingerprint. Create the project with `--environment-lock SHA256` to use
  the approved managed Linux Clang binding. A Ninja-only environment lock
  requires a matching `settings.toolchain.generator` in the SDK manifest.
- Configuration evaluates extension defaults, ordered `policyFiles`,
  project `settings`, one named profile, matrix choices, then explicit
  `--overrides FILE`. Each layer replaces fields individually. Instrumentation
  lists replace the lower list entirely. The lock records each field's origin.
  Named profiles have no inheritance.
- The matrix expands axes in ordinal field order and values in declared order.
  Include rules add alternatives relative to the profile baseline. Exclude
  rules match partial values against the expanded matrix and includes. The
  pre-exclusion expansion is limited to 32 variants and 96 workflow nodes.
  Invalid and duplicate effective variants are rejected. An incompatible
  combination can be removed with an explicit exclude rule.
- The workflow engine owns dispatch, cancellation and recovery. Configure
  admission accepts only typed policy values and carries the variant ID in
  `run.json` and configure provenance. CMake validates and applies the values
  on SDK targets.

## Identity and verification

`plan` only reads inputs. `lock` recomputes the plan and checks the reviewed
SHA-256. It stages one lock and publishes its generation with a directory
rename to `cmwf-config/<sha256>/lock.json`. Repeating publication verifies the
existing generation. Old approvals are never overwritten. A lock covers the
SDK lock, SDK inputs, configuration manifest, chosen policy files, explicit
override file and extension module. A change anywhere in the selected
manifest changes the approval even when effective values are identical.

The variant ID hashes that evidence together with the evaluator version,
effective configuration and explicit environment identity or ambient marker.
Build trees are `out/build/<Configuration>-<variant-sha256>`. Checkout paths,
source revision and output contents are separate from this identity. A
regular C++ source edit keeps the same variant. Configure/build/test and
recovery revalidate the lock and input content. A configured CMake cache
must carry the variant ID and the approved settings. Managed environments
add their existing host-local fingerprint and build-tree checks.

Generated managed workflows use an explicit toolchain binding without a
configure preset. Ambient variants use native Ninja. The existing standalone
presets work without an evaluated configuration. User-owned target-local
CMake settings remain available and fall outside the portable policy's
guarantees.

## Capability matrix

| Environment | Supported configurations | Optional policies |
| --- | --- | --- |
| Native ambient Ninja | Debug, Release, RelWithDebInfo, MinSizeRel. Static/shared for Library | GNU/Clang frontends have explicit optimization and debug-info switches. IPO requires CMake's compiler/linker probe. Linux GNU also admits ASan and UBSan, individually or together, after a compile/link/run probe. |
| Managed Linux x64 Clang with P2 lock | Native GNU x64, Ninja or Ninja Multi-Config. Static/shared for Library | Explicit Clang optimization and debug-info switches. IPO requires CMake's probe. |
| Ambient MSVC frontend | Basic native shapes when the executor has an activated environment | Optimization and enabling debug info have MSVC-specific flags. Explicit debug-info disabling is refused. |

Instrumented scaffolds build SDK-owned targets with the sanitizer flags,
including the final smoke executable. They omit install and CPack rules.
TSan, managed sanitizer runtimes, explicit installed MSVC bindings, and
cross targets are outside this release's variant admission. IPO combined
with instrumentation is refused. Applications reject explicit linkage.

See the [configuration schema](../../schemas/project-sdk/configuration.v1.schema.json)
and [lock schema](../../schemas/project-sdk/configuration-lock.v1.schema.json).
P4 can apply these contracts to Runtime, Qt, Graphics, Interop and Embedded
SDKs after defining their compiler, packaging and instrumentation capabilities.

The acceptance script takes the path to a built CLI. On Linux, pass
`--sanitizers` for GCC IPO and runtime checks. Set `CMAKEWORKFLOW_TOOL_STORE`
and `P3_ENV_LOCK` to send the managed variant through the same test. The
optional final argument names a CLI published with `RecoveryFaultInjection=true`.

```sh
bash tests/infrastructure/configuration/run-tests.sh /path/to/CMakeWorkflow --sanitizers
```
