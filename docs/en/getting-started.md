# Get started in five minutes

**[English](getting-started.md) · [Русский](../ru/getting-started.md) · [Index](README.md)**

The example works from a source checkout and demonstrates planning, configuring, compiling, executing and testing a CMake project. You do **not** have to integrate CMakeWorkflow into your own CMake build to use it.

## Prerequisites

- .NET **10 SDK**, CMake **3.26+**, Ninja and a C++ compiler.
- Run the commands below from the repository root. Substitute a platform-appropriate executable when publishing NativeAOT.

## Build and plan

```sh
dotnet build CMakeWorkflow.slnx -c Release
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow plan examples/hello/workflow.json --json
```

Planning reads existing build-tree metadata and checks the workflow graph. It does **not** implicitly configure or compile your project. A managed context gets its configure operation from an explicit workflow node.

## Run and inspect

```sh
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow run examples/hello/workflow.json --results-dir out/results --json
```

The example defines `configure → build → run` and `build → ctest`. Output uses **JSON Lines**: parse one object per line, and read the `runDirectory` value instead of guessing the generated path. Then inspect the persisted report:

```sh
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow report RUN_DIRECTORY --json
```

`run.json` is the authoritative saved status. `events.jsonl` and process stdout/stderr are additional artifacts. Exit code 0 from `workflow report` means the report was read, **not** that its workflow succeeded.

## Adopt an existing CMake build tree

If an IDE or another tool has already configured a build directory, inspect it without rebuilding:

```sh
dotnet bin/Release/net10.0/CMakeWorkflow.dll context inspect BUILD_DIR --json
dotnet bin/Release/net10.0/CMakeWorkflow.dll target resolve --build-dir BUILD_DIR --target EXECUTABLE --json
```

For an existing configured tree, use `mode: "adopt"` and `provenancePolicy: "allowUnverified"` in the workflow context, and omit the `configure` node. This acknowledges incomplete provenance; it does **not** ignore known source, generator or configuration conflicts. `target run` requests a native build before executing the target; it never assumes that a previously existing executable is current.

## Next steps

Read [workflow manifests](workflows.md) for dependencies and build-tree rules, [results and History](results-and-history.md) for saved data, and [recovery](recovery.md) before operating workflows with non-idempotent side effects. For integrated tools, [VS Code](integrations.md#vs-code-extension) and the [local Host](host-ipc.md) are optional; the CLI works independently.
