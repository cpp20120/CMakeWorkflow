# Workflow manifests

**[English](workflows.md) · [Русский](../workflows.md) · [Index](README.md)**

The manifest contract is implemented in [WorkflowModel.cs](../../CMakeWorkflow.CMake/WorkflowModel.cs). The CLI accepts `schemaVersion` **1** and **2**, rejects unknown JSON properties, and uses version 2 in new examples.

## A complete example

[examples/hello/workflow.json](../../examples/hello/workflow.json) and its [CMakePresets.json](../../examples/hello/CMakePresets.json) contain:

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

Relative `sourceDirectory`, `buildDirectory` and explicit `workingDirectory` fields are resolved **relative to the manifest file**. Relative CLI option paths normally resolve from the process working directory instead.

## Context fields

| Field | Meaning |
|---|---|
| `buildDirectory` | Required native build tree |
| `sourceDirectory` | Source tree (needed for configure) |
| `configuration` | Requested configuration, for example Debug or Release |
| `configurePreset` | Literal CMake preset name; never inferred from cache |
| `generator` | Expected generator identity, **not** a way to provide `-G` |
| `mode` | `observe`, `adopt`, `managed` |
| `provenancePolicy` | `strict` (default), `allowUnverified` |

`observe` only inspects. `adopt` permits use of an existing configured tree; `allowUnverified` is generally necessary if provenance is unknown. It does **not** waive known source/generator/configuration conflicts. `managed` requires an explicit `configure` node and appropriate dependencies before operations that use the resulting tree. Configuration is never inferred or performed implicitly. Without an explicit mode, the planner selects managed when a selected configure node or recorded managed configure exists; otherwise it selects observe.

## Node kinds

| `kind` | Required relationship and effect |
|---|---|
| `inspect` | Read the named context, no configure/build |
| `configure` | Explicit CMake configure for context |
| `build` | Native executable or library `target` in a `context`; utility targets excluded |
| `invoke` | Intentionally execute a native utility, custom or aggregate `target` |
| `run` | `from` a build node; execute its verified executable |
| `profile` | `from` a build node; capture using a profiler backend |
| `ctest` | A `context` with optional `testRegex` / `labelRegex`; requires a build/invoke predecessor for the same tree/configuration |

`from` automatically declares the build dependency for `run` and `profile`, and replaces their own `target`/`context`. Every node has a unique `id`; allowed ASCII characters are letters, digits, `.`, `_` and `-`, except an ID cannot be `.` or `..`. Common fields: `dependsOn`, `resources`, `exclusive`, `timeoutSeconds`. The timeout must be positive and no greater than 30 days.

`arguments` is an argv array, `environment` contains explicit overrides, and `workingDirectory` names the operation directory. These execution fields are forbidden on inspect/configure/build/invoke; CTest does not accept `target`, `from` or `arguments`. Only `profile` accepts `options` (`backend`, `mode`, `captureTimeoutSeconds`), with modes validated by the selected backend.

An `invoke` node can specify `harnessDirectory`: a **relative** path inside its build tree, with no absolute path or `.`/`..`/empty segments. It refers to a fresh DagFlow harness directory and has strict output-ownership rules; see [DagFlow harness](profiling-and-campaigns.md#dagflow-harness).

## Plan and execute

```sh
CMakeWorkflow workflow plan examples/hello/workflow.json --only run --only test --json
CMakeWorkflow workflow run examples/hello/workflow.json --only run --only test --jobs 2 --results-dir out/results --json
```

`--only` selects the **dependency closure**, not just the named node. The entire graph must still be structurally valid. `workflow plan` never invokes configure or build; consider process exit code, `valid` and diagnostics separately. A valid plan is not a promise that an eventual run will succeed. `exclusive`, named resources and build-tree leases cannot prevent an unrelated IDE or Ninja process from modifying the tree.
