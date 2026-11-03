# Explicit Windows toolchain execution binding

The optional `contexts.<name>.toolchain` field in a workflow manifest selects an **execution environment**, not a
preferred value for the Host's global PATH. Without a binding, existing workflow behavior is unchanged.

```json
{
  "schemaVersion": 2,
  "contexts": {
    "main": {
      "buildDirectory": "out/build/msvc-ninja",
      "sourceDirectory": ".",
      "mode": "managed",
      "toolchain": {
        "cmakeCandidateId": "<toolchains/discover ID>",
        "ninjaCandidateId": "<toolchains/discover ID>",
        "compilerCandidateId": "<toolchains/discover ID>",
        "sdkCandidateId": "<toolchains/discover ID>",
        "environmentProfileId": "vs:<VS instance ID>",
        "generator": "Ninja",
        "hostArchitecture": "x64",
        "targetArchitecture": "x64"
      }
    }
  },
  "nodes": [
    { "id": "configure", "kind": "configure", "context": "main" },
    { "id": "build", "kind": "build", "context": "main", "target": "app", "dependsOn": ["configure"] },
    { "id": "run", "kind": "run", "from": "build" }
  ]
}
```

The same binding can be provided to `target/prepare` as `toolchain` or to
`profile/prepare` on each variant (`variants[].toolchain`). The .NET SDK exposes `WorkflowToolchainBinding` for both.

## Lifecycle and guarantees

1. `toolchains/discover` lists candidates. Use the returned IDs, not guessed paths.
2. `toolchains/resolve` previews structural compatibility. A compiled project is not yet verified.
3. On `workflow/start`, before dispatch authorization, the Host resolves the IDs again, captures one per-build-tree
   environment via the selected `VsDevCmd` and checks the current CMake cache. Nothing is installed automatically.
4. `configure` uses the chosen absolute CMake executable, explicit generator, `CMAKE_CXX_COMPILER` and Ninja path. The
   environment overlay is passed only to child processes; `build`, `invoke`, `ctest`, `run` and `profile` reuse that
   same per-tree snapshot.
5. `run.json` stores tool paths, SDK, profile and environment fingerprint; it never persists the environment values. A
   changed fingerprint rejects resume **before** the resumed execution is claimed.
6. A configured tree is never re-targeted to a different generator/compiler/Ninja. The workflow is rejected instead; use
   a fresh build tree.

## Scope (deliberately limited)

- The execution adapter currently implements **Ninja / Ninja Multi-Config and x64 -> x64** on Windows. A Visual Studio
  generator needs its own instance/architecture adapter, and cross-architecture toolchains must be modelled separately.
- Explicit toolchain and `configurePreset` cannot be combined yet: evaluating inherited CMake presets without running
  configure requires more provenance evidence.
- CMake is given an absolute C++ compiler; native C compiler/toolchain-file behavior is not inferred. Actual CMake
  configuration/build validates target compatibility. CMakeLists.txt may legitimately select additional tools.
- Only allowlisted `VsDevCmd` build variables are injected; no arbitrary inherited secrets are returned by RPC or
  written to reports. Child processes still inherit the Host's regular environment for unrelated variables.
- Candidate exe files are hashed for the runtime identity. This is not cryptographic attestation of the complete SDK
  tree or of every DLL the toolchain loads. Manual external edits to build trees remain outside the tool's locking
  protocol.
- On Linux/macOS a Windows-specific binding is refused; existing native workflows remain usable unchanged.
- This is an execution capability, **not** package installation. `Doctor/Fix` and `environment/plan` remain separately
  approved remediation operations.
