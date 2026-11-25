# Engineering Kits

[Programmable Kit campaigns and History gates](programmable-engineering.md).

An engineering Kit selects a toolchain and platform for a project. Select a configuration profile separately to choose build type, linkage, optimization, instrumentation and testing. The execution placement is the Host connection and workspace.

| Built-in Kit | Compiler | Generator | Execution host and target |
| --- | --- | --- | --- |
| `linux-clang` | Managed Clang, LLD and sysroot | Ninja | Linux x64, GNU x64 |
| `linux-clang-multi` | Managed Clang, LLD and sysroot | Ninja Multi-Config | Linux x64, GNU x64 |
| `linux-gcc` | System GCC and G++ | Ninja | Linux x64, GNU x64 |
| `linux-system-clang` | System Clang and Clang++ | Ninja | Linux x64, GNU x64 |
| `windows-msvc` | MSVC and Windows SDK selected on the Host | Ninja | Windows x64, MSVC x64 |
| `macos-appleclang` | AppleClang and macOS SDK selected by Xcode | Ninja | macOS ARM64, Darwin ARM64 |

Ambient Kits resolve and verify compiler family, version and architecture on the Host. Their fingerprints include the selected executables and relevant environment. System libraries and SDKs remain host-dependent. Qt and Graphics use ambient Qt/Vulkan dependencies and their existing CMake checks. Cross and embedded Kit providers are outside this capability matrix. The existing no-Kit commands continue to work on their supported platforms.

## Select and approve

```sh
cmakeworkflow new runtime Demo
cd Demo
cmakeworkflow kit list
cmakeworkflow kit show linux-clang
cmakeworkflow kit use linux-clang
cmakeworkflow restore --plan --json
```

Review the Kit digest, then run `cmakeworkflow kit lock linux-clang --plan-sha KIT_DIGEST`. For managed Kits, run `cmakeworkflow project tooling plan` and, if tools are missing, review and apply it with `cmakeworkflow project tooling setup --yes --plan-sha TOOLING_DIGEST`. Prefix the plan's `environmentApprovalCommand` with `cmakeworkflow` to publish the managed environment lock after verification. Existing SDK environment pins must match the Kit. An environment pin inherited from the SDK is verified through the existing tooling commands.

Run `cmakeworkflow restore --plan` again after Kit approval. Review the configuration plan and run `cmakeworkflow restore --configuration-plan-sha CONFIG_DIGEST` with the same configuration selection. For example, repeat that approval for `--profile debug` or `--matrix`. Execute once the required approvals and tools are in place.

```sh
cmakeworkflow restore
cmakeworkflow build --profile release
cmakeworkflow test --profile release
cmakeworkflow benchmark --profile release --repeats 3
cmakeworkflow profile --target Demo_bench --backend perf
cmakeworkflow pack
```

All project operations accept `--kit ID`, which takes precedence over the project-local active selection. `kit current` shows that selection, and `kit clear` restores the no-Kit default. Selection and clearing affect only the active state. `kit plan` resolves requirements, `kit verify` reviews the current definition and checks the approved lock, and `kit doctor` inspects local prerequisites. Use `--project DIRECTORY` to select a project explicitly and `--json` for structured CLI output.

Project definitions in `cmwf.kits.json` follow the [catalog schema](../../schemas/project-sdk/engineering-kits.v1.schema.json). IDs are lower-case and unique. Built-in IDs are reserved. Custom definitions choose a supported provider and generator and may pin exact managed component versions. User-level catalog discovery is deferred. Use `benchmark --kit-matrix ID,ID` to compare two to eight distinct Kits on one execution platform. Approve each Kit and configuration separately. The operation records the Kit and configuration axes and serializes measurements.

## Evidence and execution

The Developer API resolves Kit definitions and publishes approved Kit locks under `cmwf-kits/`. CMake composition uses configuration lock v2, keeping the SDK lock, no-Kit v1 lock identities, and existing CMake module bytes intact. The Host verifies the Kit and configuration evidence and resolves the concrete toolchain before execution. Existing build-tree provenance rejects incompatible cached compiler or tool identities. The Creator and lifecycle API remain in `CMakeWorkflow.Developer`; CLI commands are adapters.

`PlanOperationAsync` returns the reviewed selection and exact manifest digest. Supply that plan in `DeveloperExecutionRequest.ReviewedPlan` when submitting after an active selection change. On a remote Host, stage the source, SDK lock and inputs, Kit and configuration lock directories, and digest-addressed manifest together. Use `PublishOperationManifestAsync(request, reviewedPlan)` to prepare the manifest and `ExecuteStagedOperationAsync` with the explicit Host manifest/workspace paths and an existing Client SDK SSH connection. Tool installations and environment locks belong to the execution Host.

Keep the admission ID and the original manifest, configuration and Kit generation for recovery. Resume uses admitted evidence through existing Host reconciliation APIs. Kit switches change neither prior evidence nor its build tree. Missing or modified evidence causes verification to fail. For vcpkg projects, use a managed Linux Clang Kit and approve the installation plan emitted by `restore --plan` with `tooling vcpkg install . --yes --plan-sha DIGEST`. Generated workflows bind that receipt automatically. The Host verifies the project path, declarations, toolchain releases and installed tree before dispatch. Existing local vcpkg receipts are tied to the execution workspace path. Use the existing snapshot-based experiment flow for portable remote dependency receipts.

## Benchmark comparisons

Run the same benchmark separately for both approved Kits, using one History database.

```sh
db="$PWD/out/kit-history.sqlite"
cmakeworkflow benchmark --kit linux-gcc --profile release --repeats 5 --history-db "$db"
cmakeworkflow benchmark --kit linux-clang --profile release --repeats 5 --history-db "$db"
cmakeworkflow history runs --metric elapsed --db "$db"
cmakeworkflow history compare BASELINE_ID CANDIDATE_ID --db "$db"
```

Inspect the per-node Kit SHA and toolchain fingerprints in the corresponding workflow reports to distinguish Kit, configuration and Host differences. History includes those identities in indexed measurement environments, allowing comparability checks to flag different engineering environments.

For dogfooding, switch a Runtime project between managed Clang and GCC, compare repeated trials in one History database, then stage the same managed evidence onto two compatible Hosts. Check recovery after an interrupted configure or test while another Kit is active.
