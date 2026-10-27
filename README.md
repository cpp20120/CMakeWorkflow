# CMakeWorkflow

CMakeWorkflow is a standalone .NET 10 CLI for orchestrating work in CMake
projects. It plans explicit workflows, asks CMake to configure and build native
targets, runs executables and CTest, and records results. It works with existing
build trees and does not require a particular C++ library, repository layout, or
CMake toolkit.

CMake and its generator remain responsible for native dependencies, compiler
flags, incremental builds, presets and test registration. CMakeWorkflow owns the
workflow graph, process lifecycle, build-tree coordination, provenance checks
and run artifacts.

## Build and try it

Requirements: .NET 10 SDK and CMake 3.26 or newer. The included example and
integration tests also use Ninja and a C++ compiler.

```sh
dotnet build CMakeWorkflow.slnx -c Release
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow plan examples/hello/workflow.json
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow run examples/hello/workflow.json --results-dir out/results
dotnet bin/Release/net10.0/CMakeWorkflow.dll workflow report out/results/<run-directory>
```

Publish a native executable with the RID for your host, for example:

```sh
dotnet publish CMakeWorkflow.csproj -c Release -r linux-x64 -o out/native
out/native/CMakeWorkflow workflow plan examples/hello/workflow.json
```

The example is a small managed workflow: configure, build, execute and run
CTest. For an existing IDE tree, use `mode: "adopt"` and
`provenancePolicy: "allowUnverified"`, omit the configure node and point
`buildDirectory` at that tree.

## Commands

```sh
CMakeWorkflow context inspect BUILD_DIR
CMakeWorkflow target build --build-dir BUILD_DIR --target TARGET
CMakeWorkflow target run --build-dir BUILD_DIR --target EXECUTABLE -- ARG...
CMakeWorkflow target invoke --build-dir BUILD_DIR --target CUSTOM_TARGET
CMakeWorkflow target resolve --build-dir BUILD_DIR --target EXECUTABLE
CMakeWorkflow workflow plan FILE
CMakeWorkflow workflow run FILE --results-dir DIR
CMakeWorkflow workflow report RUN_DIRECTORY
```

`workflow plan` validates the selected graph and reads existing build-tree
data without building. `workflow run` executes its dependency closure, retains
logs and writes a `run.json` journal. `target build` requests a known native
target; `target invoke` intentionally executes a custom or aggregate target.
`target resolve` only reads the CMake File API codemodel.

## Workflow contract

Schema version 2 supports `inspect`, `configure`, `build`, `invoke`, `run`,
`profile` and `ctest` nodes, dependency closure, resource constraints,
timeouts and cancellation. Schema version 1 remains accepted. Managed contexts
require an explicit configure node. Adopted contexts use an existing tree
without configure provenance. Strict and `allowUnverified` policies control
whether execution may continue after provenance becomes uncertain. Known
source, generator and configuration conflicts always fail.

Every build request reaches CMake's backend. A dependent `run` node executes an
artifact from a successful build node and checks its recorded identity. An
existing executable file does not prove its target is current. The CLI never
infers a configure preset from cache entries or starts configure, clean or
rebuild implicitly.

CMakeWorkflow keeps configure provenance and optional bridge manifests under
`cmake-workflow/` in each build tree. Its leases coordinate only other
CMakeWorkflow processes. An IDE or independent CMake invocation can still
change that tree. File API generation changes can downgrade provenance even
when observed configuration properties remain equivalent; neither a successful
build nor a recorded configure invocation proves a hermetic environment.

The optional [CMake target bridge](cmake/CMakeWorkflowTargetBridge.cmake) lets a
custom target invoke a prepared executable without recursively entering its
own build tree. Set `WORKFLOW_RUNNER_EXECUTABLE` to the published CLI before
configuring a project that uses the adapter. The CLI itself works without the
adapter; CMake File API data is its normal target source.

## Projects and checks

- `CMakeWorkflow.Core/` provides process execution, cancellation, hashing and
  generic operation contracts without CMake or project-specific dependencies.
- `CMakeWorkflow.CMake/` handles File API inspection, targets, build-tree
  ownership and the workflow graph.
- `CMakeWorkflow.Profiling/` provides platform capture and analysis operations.
- `CMakeWorkflow.Campaigns/` provides optional project-specific campaigns.
- `tests/`, `CMakeWorkflow.Profiling.Tests/` and
  `CMakeWorkflow.Campaigns.Tests/` contain executable checks.

The generic integration suite creates temporary CMake/Ninja projects and
checks graph execution, context lifecycle, cancellation, leases, regeneration,
multi-config resolution and read-only reporting:

```sh
dotnet run --project tests/CMakeWorkflow.IntegrationTests.csproj -c Release -- bin/Release/net10.0/CMakeWorkflow
```

Unix SIGINT cases run on Unix hosts. Windows cancellation behavior requires
execution on a Windows runner.
