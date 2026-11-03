# Workflow manifests

**[English](workflows.md) · [Русский](../workflows.md) · [Index](README.md)**

The manifest contract is implemented in [WorkflowDocument.cs](../../CMakeWorkflow.Contracts/Workflows/WorkflowDocument.cs). The CLI accepts
`schemaVersion` **1** and **2**, rejects unknown JSON properties, and uses version 2 in new examples.

## A complete example

[examples/hello/workflow.json](../../examples/hello/workflow.json) and
its [CMakePresets.json](../../examples/hello/CMakePresets.json) contain:

```json
{
  "schemaVersion": 2,
  "contexts": {
    "hello": {
      "sourceDirectory": ".",
      "buildDirectory": "../../out/hello",
      "configuration": "Debug",
      "configurePreset": "debug",
      "mode": "managed",
      "provenancePolicy": "strict"
    }
  },
  "nodes": [
    { "id": "configure", "kind": "configure", "context": "hello" },
    { "id": "build", "kind": "build", "context": "hello", "target": "hello", "dependsOn": ["configure"] },
    { "id": "run", "kind": "run", "from": "build" },
    { "id": "test", "kind": "ctest", "context": "hello", "labelRegex": "smoke", "dependsOn": ["build"] }
  ]
}
```

Relative `sourceDirectory`, `buildDirectory` and explicit `workingDirectory` fields are resolved **relative to the
manifest file**. Relative CLI option paths normally resolve from the process working directory instead.

## Context fields

| Field              | Meaning                                                    |
|--------------------|------------------------------------------------------------|
| `buildDirectory`   | Required native build tree                                 |
| `sourceDirectory`  | Source tree (needed for configure)                         |
| `configuration`    | Requested configuration, for example Debug or Release      |
| `configurePreset`  | Literal CMake preset name; never inferred from cache       |
| `generator`        | Expected generator identity, **not** a way to provide `-G` |
| `mode`             | `observe`, `adopt`, `managed`                              |
| `provenancePolicy` | `strict` (default), `allowUnverified`                      |

`observe` only inspects. `adopt` permits use of an existing configured tree; `allowUnverified` is generally necessary if
provenance is unknown. It does **not** waive known source/generator/configuration conflicts. `managed` requires an
explicit `configure` node and appropriate dependencies before operations that use the resulting tree. Configuration is
never inferred or performed implicitly. Without an explicit mode, the planner selects managed when a selected configure
node or recorded managed configure exists; otherwise it selects observe.

## Node kinds

| `kind`      | Required relationship and effect                                                                                          |
|-------------|---------------------------------------------------------------------------------------------------------------------------|
| `inspect`   | Read the named context, no configure/build                                                                                |
| `configure` | Explicit CMake configure for context                                                                                      |
| `build`     | Native executable or library `target` in a `context`; utility targets excluded                                            |
| `invoke`    | Intentionally execute a native utility, custom or aggregate `target`                                                      |
| `run`       | `from` a build node; execute its verified executable                                                                      |
| `profile`   | `from` a build node; capture using a profiler backend                                                                     |
| `ctest`     | A `context` with optional `testRegex` / `labelRegex`; requires a build/invoke predecessor for the same tree/configuration |

`from` automatically declares the build dependency for `run` and `profile`, and replaces their own `target`/`context`.
Every node has a unique `id`; allowed ASCII characters are letters, digits, `.`, `_` and `-`, except an ID cannot be `.`
or `..`. Common fields: `dependsOn`, `resources`, `exclusive`, `timeoutSeconds`. The timeout must be positive and no
greater than 30 days.

`arguments` is an argv array, `environment` contains explicit overrides, and `workingDirectory` names the operation
directory. These execution fields are forbidden on inspect/configure/build/invoke; CTest does not accept `target`,
`from` or `arguments`. Only `profile` accepts `options` (`backend`, `mode`, `captureTimeoutSeconds`), with modes
validated by the selected backend.

An `invoke` node can specify `harnessDirectory`: a **relative** path inside its build tree, with no absolute path or
`.`/`..`/empty segments. It refers to a fresh DagFlow harness directory and has strict output-ownership rules;
see [DagFlow harness](profiling-and-campaigns.md#dagflow-harness).

## Declared experiment artifacts and execution requirements

Schema v2 nodes may declare `inputs`, `outputs` and `constraints`. These are optional and do not change old manifests.
For example:

```json
{
  "id": "build-model", "kind": "build", "context": "research", "target": "model",
  "inputs": [{"path": "../src/model.cpp", "sha256": "<64 hex digits>", "snapshot": true}],
  "outputs": [{"name": "model-report", "path": "../out/model/report.json"}]
}
```

Input `path` is relative to the manifest unless absolute. An input may instead use
`{"from":"producer-node","artifact":"output-name"}`; this adds an ordinary DAG dependency and checks the producer's
recorded hash again before the consumer. With both `from` and `path`, the live path must match that hash.
`sha256` pins an expected value; without it, the runner records the value it observed. Inputs are checked before and
after the node. `snapshot:true` copies the observed file or directory into the run directory and verifies the copy.
Symlinks and reparse points in declared artifacts are rejected.

An output has a unique `name` and a `path` relative to the manifest by default.
`"root":"run"` makes it relative to the run directory. The runner requires the output to exist after success and records
its SHA-256 in `run.json` and workflow events. `"fresh":true` additionally refuses a pre-existing path before dispatch;
omit it for valid incremental CMake outputs. This is a declared output check, **not** a native build freshness
predicate. Resume rechecks recorded artifacts. Neither hashing nor snapshots continuously lock external writers or
attest the complete compiler environment. An interrupted node with artifact declarations or execution constraints cannot
be manually reconciled as `succeeded`: that decision cannot reconstruct the required checks. Reconcile it as failed or
explicitly retry according to the existing recovery protocol.

`constraints.cpuSet` is supported on `run` and `profile` nodes and requires a numeric CPU list such as `"2,4-5"`. On
Linux the runner checks the host allowance and launches through `taskset` so affinity is applied before target exec; it
records the requested and allowed sets. On hosts without this launch guarantee it refuses the node. A profile node may
also require `constraints.profiler` with exact `backend`, `mode` and optional
`eventSource`; these must match explicit profile options and the selected backend's capabilities before capture starts.
Profiler permissions and actual event availability can still fail during capture and are reported as failures. These
constraints do not assert fixed CPU frequency, isolation or comparable counter semantics across platforms.

## Plan and execute

```sh
CMakeWorkflow workflow plan examples/hello/workflow.json --only run --only test --json
CMakeWorkflow workflow run examples/hello/workflow.json --only run --only test --jobs 2 --results-dir out/results --json
```

`--only` selects the **dependency closure**, not just the named node. The entire graph must still be structurally valid.
`workflow plan` never invokes configure or build; consider process exit code, `valid` and diagnostics separately. A
valid plan is not a promise that an eventual run will succeed. `exclusive`, named resources and build-tree leases cannot
prevent an unrelated IDE or Ninja process from modifying the tree.
