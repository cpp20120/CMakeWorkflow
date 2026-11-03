# Typed workflow recipes

**[English](recipes.md) · [Русский](../ru/recipes.md) · [Index](README.md)**

A recipe is typed input to a builder. It is **not** an executable workflow or a
second manifest format. `BenchmarkRecipeV1` has its own `kind: "benchmark"` and
`schemaVersion: 1`, independently of the generated `WorkflowDocument` version 2.
The [recipe schema](../../schemas/recipes/benchmark.v1.schema.json) documents
its JSON shape; the C# builder also checks host-dependent path rules. Profile,
test-matrix and fuzz recipes may add separate contracts later. Existing profile
commands keep their current contract.

## Generate, inspect and run

From a project directory with an **already configured** CMake build tree:

```sh
CMakeWorkflow workflow generate benchmark \
  --build-dir out/build/release --target my_benchmark --config Release \
  --repeats 10 --timeout-seconds 300 --output workflows/chain.json \
  -- --benchmark chain --workers 2
CMakeWorkflow workflow plan workflows/chain.json --json
CMakeWorkflow workflow run workflows/chain.json --jobs 8 --results-dir out/results --json
CMakeWorkflow workflow report RUN_DIRECTORY --json
```

Replace `RUN_DIRECTORY` with the actual `runDirectory` from the JSONL stream.
Generation only writes the manifest; it does not configure, build or run.
Relative `--build-dir` and `--output` paths resolve from the CLI process working
directory. The manifest stores an absolute build path, so the location of the
output file does not change the selected build tree. Paths containing spaces
are passed as individual CLI arguments. Everything after `--` becomes the
executable's argv array without shell parsing. Required options are
`--build-dir`, `--target`, `--repeats`, `--timeout-seconds`, and `--output`.
An existing output file is rejected; generation never overwrites it. A
temporary file in the output directory is renamed to the final path only
after a complete write.

The builder emits one adopted context (`allowUnverified`), one build node and
`timing_001` through `timing_N`. Each timing node depends on its predecessor
and has the `measurement-host` resource, so `--jobs 8` does not overlap them.
All timing nodes use `from: "build"`; the existing runner still asks CMake to
verify/build the target and checks its executable identity. The adopted tree
must already be configured; known provenance conflicts still fail in the
planner. The run journal records **uninstrumented process wall time**, not the
benchmark's internal metrics or a profiler trace. Ten repeats alone do not
establish statistical significance; History compare and detect are separate
operations with their own compatibility checks.

## Other recipe families

Every recipe JSON has its own `kind` and `schemaVersion: 1`. All builders emit
the **same ordinary `WorkflowDocument` v2**. A recipe file is input to
`workflow generate`; `workflow plan` and `workflow run` consume the generated
manifest. Generation performs no configure, build, test, benchmark or capture.
After any generation command, use the same `workflow plan`, `workflow run`,
and `workflow report` sequence shown above.
Direct CLI paths are resolved from the caller's cwd; relative paths inside a
recipe JSON are resolved from the recipe file's directory. Host recipes require
absolute paths inside the initialized workspace. Existing output manifests
are never overwritten.

### Profile

Schema: [profile.v1.schema.json](../../schemas/recipes/profile.v1.schema.json).
Specify the *capture platform* explicitly so generation on a different OS
cannot change the graph. `auto` resolves to `perf` on Linux, `etw` on Windows,
or `instruments` on macOS. Running on another platform does not retarget the
capture. Existing `profile run` and `profile/prepare` retain their contracts.

```sh
CMakeWorkflow workflow generate profile --build-dir out/build/release \
  --target my_benchmark --config Release --platform linux --backend auto \
  --mode cpu --repeats 3 --timeout-seconds 300 --output workflows/profile.json \
  -- --case chain
CMakeWorkflow workflow plan workflows/profile.json
CMakeWorkflow workflow run workflows/profile.json --results-dir out/profile-results
CMakeWorkflow profile analyze RUN_DIRECTORY
```

Use repeated `--variant name=DIR` instead of `--build-dir` for multiple
configured trees. JSON also supports `sourceDirectory` and `configurePreset`
for one explicitly managed variant. Example recipe:

```json
{"kind":"profile","schemaVersion":1,"variants":[{"name":"main","buildDirectory":"../out/build/release"}],"target":"my_benchmark","configuration":"Release","repeats":3,"timeoutSeconds":300,"arguments":["--case","chain"],"backend":"auto","mode":"cpu","platform":"linux"}
```

The existing profile builder produces builds, uninstrumented timing passes,
and exclusive profiler captures. Linux CPU mode also emits a separate counter
pass; diagnostic mode may emit scheduler captures. Captures use
`measurement-host`, depend on previous measurements and retain distinct
backend semantics. Generated manifests run through ordinary `workflow run`;
saved captures can be analyzed separately. The CLI currently also analyzes
successful profile captures after `workflow run`, as it did before recipes.

### Test Matrix

Schema: [test-matrix.v1.schema.json](../../schemas/recipes/test-matrix.v1.schema.json).
Each group selects exactly one CTest `testRegex` or `labelRegex`.

```sh
CMakeWorkflow workflow generate test-matrix --build-dir out/build/release \
  --target my_tests --config Release --label unit --label integration \
  --order parallel --timeout-seconds 300 --output workflows/tests.json
```

```json
{"kind":"test-matrix","schemaVersion":1,"buildDirectory":"../out/build/release","target":"my_tests","configuration":"Release","groups":[{"name":"unit","labelRegex":"unit"},{"name":"integration","testRegex":"integration_.*"}],"executionOrder":"parallel","timeoutSeconds":300}
```

The graph is `build_tests → {ctest_unit, ctest_integration}`. `parallel`
permits independent groups to overlap when the runner allows it; `sequential`
chains them in recipe order. Each CTest node has the build prerequisite in
the same context. The timeout applies to each CTest process, not individual
test cases. Group names must be unique lowercase IDs; the limit is 64 groups.
CTest remains responsible for test discovery and matching. Failed builds
block their CTest descendants.

### Build Matrix

Schema: [build-matrix.v1.schema.json](../../schemas/recipes/build-matrix.v1.schema.json).

```sh
CMakeWorkflow workflow generate build-matrix \
  --variant debug=out/build/debug --variant release=out/build/release \
  --target my_tests --target my_benchmark --output workflows/builds.json
```

```json
{"kind":"build-matrix","schemaVersion":1,"variants":[{"name":"debug","buildDirectory":"../out/build/debug","configuration":"Debug"},{"name":"release","buildDirectory":"../out/build/release","configuration":"Release"}],"targets":["my_tests","my_benchmark"]}
```

Each variant creates an adopted context; each variant/target pair creates one
`build` node (`build_debug_001`, etc.). Variant names are identifiers only;
they do not set flags, compilers or presets. The trees must already be
configured. Independent trees may build concurrently. The existing runner
serializes operations that share a physical build tree, including aliases.
Literal duplicate directories are rejected during generation; the planner
enforces physical ownership. The limit is 512 build nodes. This version does
not generate managed configure nodes.

### Benchmark Matrix

Schema: [benchmark-matrix.v1.schema.json](../../schemas/recipes/benchmark-matrix.v1.schema.json).
Nested axes are supplied as typed JSON:

```json
{"kind":"benchmark-matrix","schemaVersion":1,"variants":[{"name":"o3","buildDirectory":"../out/build/o3","configuration":"Release"},{"name":"lto","buildDirectory":"../out/build/lto","configuration":"Release"}],"target":"my_benchmark","repeats":10,"timeoutSeconds":300,"fixedArguments":[],"axes":[{"name":"case","flag":"--benchmark","values":["chain","independent"]},{"name":"workers","flag":"--workers","values":["1","2","4","8","16"]}],"executionOrder":"round-robin"}
```

```sh
CMakeWorkflow workflow generate benchmark-matrix \
  --recipe recipes/benchmark-matrix.json --output workflows/matrix.json
CMakeWorkflow workflow plan workflows/matrix.json
CMakeWorkflow workflow run workflows/matrix.json --jobs 8 --results-dir out/results
CMakeWorkflow workflow report RUN_DIRECTORY --json
```

The example yields two build nodes and 200 timing nodes. Axis order and value
order determine Cartesian combination indices; the last axis varies fastest.
Each argument flag and value remains a separate argv element. IDs follow
`timing_VARIANT_COMBINATION_REPETITION`, for example
`timing_o3_0001_001`. The ID plus manifest argv identifies the scenario;
History does not infer structured axis fields from it.

`sequential` visits variant → combination → repetition. `round-robin`
visits repetition → combination → variant, reversing variant order on even
repetitions. All timing nodes form one dependency chain and hold
`measurement-host`, so even `--jobs 8` cannot overlap measurements. The
builder validates the Cartesian product before allocation and rejects more
than 4096 total nodes. Timing nodes record process duration, not internal
benchmark metrics or statistical confidence. CMake owns all native builds.

## Host preparation for new recipes

The existing `profile/prepare` remains unchanged. Four new capabilities use
one positional request object `{ "requestId": "32-hex", "outputPath":
"/workspace/workflows/name.json", "recipe": { ... } }`:

| Recipe | Capability | Typed request |
|---|---|---|
| Profile | `profileRecipe/prepare` | `ProfileRecipePrepareRequest` |
| Test Matrix | `testMatrix/prepare` | `TestMatrixPrepareRequest` |
| Build Matrix | `buildMatrix/prepare` | `BuildMatrixPrepareRequest` |
| Benchmark Matrix | `benchmarkMatrix/prepare` | `BenchmarkMatrixPrepareRequest` |

Each response has `schemaVersion: 1`, `manifest`, and `manifestSha256` for the
exact committed bytes. The Host validates all paths against its initialized
workspace and uses commit-once bindings. Retry the identical request and ID
after a lost response; conflicting parameters or output collisions fail.
Preparation does not admit or execute a workflow. Call `workflow/plan`, then
explicitly call durable `workflow/start` with the returned manifest/hash and
a separate `clientRequestId` and results directory. Editor clients send typed
recipe fields; they do not construct `WorkflowNode[]`.

Invalid kind/version, missing fields, unknown JSON properties, duplicate IDs,
bad regex selection, backend/platform mismatches, duplicate build paths and
oversized graphs fail before a final manifest is created. The JSON schemas
describe input contracts; the typed builders enforce semantic and host path
rules.

Fuzz, mutation and weak-memory remain separate typed services. Their possible
workflow integration is outlined in [future campaign recipes](campaign-recipe-future.md).

## Host and editor contract

The capability-negotiated `benchmark/prepare` method takes one typed JSON-RPC
request object (positional parameter `[request]`):

```json
{
  "requestId": "0123456789abcdef0123456789abcdef",
  "outputPath": "/workspace/workflows/chain.json",
  "recipe": {
    "kind": "benchmark",
    "schemaVersion": 1,
    "buildDirectory": "/workspace/out/build/release",
    "target": "my_benchmark",
    "configuration": "Release",
    "repeats": 10,
    "timeoutSeconds": 300,
    "arguments": ["--benchmark", "chain", "--workers", "2"]
  }
}
```

The response is `{ "schemaVersion": 1, "manifest": "/workspace/workflows/chain.json",
"manifestSha256": "..." }`. `requestId` is 32 hex characters. Host paths must
be absolute and inside the workspace established by `initialize`. A retry
with the same ID and canonical recipe returns the same committed preparation;
different parameters or output path with that ID produce
`request_id_conflict`. Concurrent calls cannot overwrite a committed file.
The returned SHA-256 covers the exact saved manifest bytes. After a lost
response, retry `benchmark/prepare` with the **same** ID and request; do not
infer that execution began. An editor can build a form from the typed recipe
fields and pass the returned path to `workflow/plan`. Explicit execution uses
the existing durable `workflow/start` with the returned manifest and hash,
a separate `clientRequestId`, and an explicit results directory. Subsequent
status, reconnect, cancellation and recovery use the existing operation API.

## .NET SDK recipe models

Recipe DTOs (`BenchmarkRecipeV1`, `TestMatrixRecipeV1`, `BuildMatrixRecipeV1`,
`BenchmarkMatrixRecipeV1` and their variants) now live in the
`CMakeWorkflow.Contracts` assembly under the `CMakeWorkflow` namespace.
`ProfileRecipeV1` and `ProfileVariant` retain the `CMakeWorkflow.Profiling`
namespace but also live in Contracts. `CMakeWorkflow.Client` references the same
models instead of defining separate wire copies. This is a **source-breaking**
namespace change for consumers using the old `CMakeWorkflow.Client.*RecipeV1`
models: add `using CMakeWorkflow;` and `using CMakeWorkflow.Profiling;`.
For selected native toolchains use `WorkflowToolchainBinding` instead of
`PrepareToolchainSelection`. The JSON-RPC method names and JSON fields do not change.
