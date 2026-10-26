# CMakeWorkflow

CMakeWorkflow is a standalone local runner for existing CMake build trees. It
uses CMake File API observations and explicit `cmake`/`ctest` requests to
schedule builds, executable runs, tests and custom targets. The .NET 10 CLI
does not need a particular C++ library or project layout.

## Build and try it

Requirements: .NET 10 SDK and CMake 3.26 or newer. The example and integration
tests use Ninja and a C++ compiler.

```sh
dotnet build CMakeWorkflow.csproj -c Release
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow plan examples/hello/workflow.json
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow run examples/hello/workflow.json --results-dir out/results
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow report out/results/<run-directory>
```

Publish a native executable with the RID for your host, for example:

```sh
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -o out/native
out/native/CMakeWorkflow workflow plan examples/hello/workflow.json
```

The optional `cmake/CMakeWorkflowTargetBridge.cmake` adapter lets a CMake
custom target invoke a prepared executable without entering its own build tree
recursively. Set `WORKFLOW_RUNNER_EXECUTABLE` to the published CLI before
configuring a project that uses the adapter. The CLI itself works without the
adapter; CMake File API codemodel data is its normal target source.

## Contract

Workflow schema version 2 supports `inspect`, `configure`, `build`, `invoke`,
`run` and `ctest` nodes, dependency closure, resource constraints, timeouts,
cancellation and a `run.json` journal. Schema version 1 remains accepted.
`workflow plan` only reads existing build tree data. Managed contexts require
an explicit configure node. Adopted contexts require `mode: "adopt"` and use
an existing tree without configure provenance. Strict and `allowUnverified`
policies control whether execution may continue after provenance becomes
uncertain. Known source, generator and configuration conflicts always fail.

`build` requests a known native target; `invoke` intentionally executes a
custom or aggregate target. `run` uses an executable produced by a successful
`build` node. Each build request reaches CMake's backend, which owns native
dependencies and incremental rebuilding. An existing executable file does not
prove that its target is current. The CLI never infers a configure preset from
cache entries or starts configure, clean or rebuild implicitly.

The tool keeps configure provenance and optional bridge manifests under
`cmake-workflow/` in each build tree. Its leases coordinate only processes of
this CLI. Other CMake tools and independent orchestration CLIs do not share
those leases; do not mutate one build tree concurrently through both. File API
generation changes can downgrade provenance even when observed configuration
properties remain equivalent. Neither a successful build nor a recorded
configure invocation proves full configuration freshness or a hermetic
environment.

## Integration checks

The C# runner creates temporary real CMake/Ninja projects and exercises the
CLI. It has no external test framework or scripting runtime dependency:

```sh
dotnet run --project tests/CMakeWorkflow.IntegrationTests.csproj -c Release -- out/native/CMakeWorkflow
```

The suite checks the workflow graph, CMake ownership boundary, build context
lifecycle, cancellation, cross-process leases, regeneration, multi-config
resolution, and read-only reporting. Unix SIGINT cases run on Unix hosts;
Windows cancellation behavior still needs execution on a Windows runner.

The included `examples/hello/workflow.json` is a small managed workflow for a
regular CMake project. For an existing IDE tree, use `mode: "adopt"` and
`provenancePolicy: "allowUnverified"`, remove the configure node and point
`buildDirectory` at that tree.
