# CMakeWorkflow documentation

**[English](README.md) · [Русский](../ru/README.md) · [Language index](../README.md)**

CMakeWorkflow is a standalone .NET 10 tool for **orchestrating work around CMake builds**. It models explicit dependency graphs, runs CMake, Ninja/native executables and CTest, manages process cancellation and build-tree coordination, and retains machine-readable evidence. CMake still owns native dependencies, compiler configuration, presets and incremental compilation.

## Start here

**[Five-minute getting started](getting-started.md)** — build the CLI, plan and run `examples/hello`, inspect results, then use an existing CMake build directory.

## User and integration guides

| Guide | What you will find |
|---|---|
| [Architecture](architecture.md) | Components, process boundaries, scheduler and provenance guarantees |
| [Workflow manifests](workflows.md) | Contexts, node types, dependencies, resources and examples |
| [CLI reference](cli.md) | Build/publish, commands and exit/JSON contracts |
| [Results and History](results-and-history.md) | JSONL, `run.json`, SQLite, comparisons and regression detection |
| [Recovery](recovery.md) | Unknown effects, explicit reconciliation, audit and durability |
| [Profiling and campaigns](profiling-and-campaigns.md) | perf, ETW, Instruments, replay and DagFlow harness/fuzz |
| [Provisioning and IDE integration](integrations.md) | Doctor/Setup, CMake bridge, VS Code, limits of tests |
| [Local Host IPC](host-ipc.md) | Local JSON-RPC, NativeAOT, Doctor/Fix, operation lifecycle |

## Design records

- [ADR-0001: Proposed MSBuild execution backend](adr/0001-msbuild-execution-backend.md) — **not implemented**.
- [ADR-0002: Local Host IPC](adr/0002-local-host-ipc.md) — transport and responsibility boundaries.

## Documentation contract

These pages describe the **current repository checkout**, not all previously distributed binaries. A successful build, an available profiler tool or a present output file alone is not evidence of a valid workload or successful profiling capture. Features with explicit caveats — especially recovery, historical measurements and native tracing — retain those caveats in both languages.

The canonical Russian reference pages remain at their original `docs/*.md` paths for existing links. Source files and schemas are authoritative when details change. Cross-repository compatibility (CLI, VS Code extension and SDK) should be validated against the versions actually installed.
