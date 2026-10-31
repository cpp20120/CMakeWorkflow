# Results, event protocol and History

**[English](results-and-history.md) · [Русский](../results-and-history.md) · [Index](README.md)**

A workflow produces a persisted authoritative report, a stream of live events and optional native artifacts. These are **different contracts**; clients must not infer a completed or successful run from the presence of a directory, an event or an executable alone.

## Independent schema versions

| Contract | Version |
|---|---|
| Workflow manifest input | `schemaVersion: 1` or `2` |
| `WorkflowPlanView`, `WorkflowEvent`, target resolution | `schemaVersion: 1` |
| `WorkflowReport` in `run.json` | `schemaVersion: 1` |
| SQLite catalog | `PRAGMA user_version = 4` |

These are not a single product version. The installed binary and client/extension compatibility must be checked separately.

## Read-only planning

[WorkflowPlanView](../../CMakeWorkflow.CMake/WorkflowProtocol.cs) includes `schemaVersion`, `workflow`, `manifestHash`, `valid` and `nodes`. Node entries expose `id`, `kind`, `target`, `buildDirectory`, `configuration`, `dependencies`, `observedState`, `diagnostics`, `resources` and `exclusive`. Diagnostics have severity and message; observation states include `Observed`, `Unverified`, `Runtime` and `Error`.

Check exit status, `valid` and diagnostics **independently**. Some invalid manifests can fail before a plan JSON is produced. A client must display a missing or malformed plan as an error.

## Live JSONL and saved state

```sh
CMakeWorkflow workflow run FILE --json --results-dir DIR
CMakeWorkflow workflow report RUN_DIRECTORY --json
```

Workflow execution streams **one JSON object per line to stdout**, with diagnostics on stderr. It first persists event lines to `events.jsonl`. Each event may contain:

| Field | Meaning |
|---|---|
| `schemaVersion` | Event protocol version |
| `sequence` | Run-local monotonically increasing sequence starting at 1 |
| `timestamp`, `type`, `runDirectory` | Timestamp, event kind, actual output location |
| `nodeId`, `nodeKind` | Nullable node identity |
| `status`, `exitCode`, `error` | Nullable operation fields |
| `artifacts` | Objects carrying `type`, `path`, optional `sha256` |

Event kinds include `workflow_started`, `workflow_resumed`, `node_started`, `node_finished` and `workflow_finished`. Node states can be `pending`, `running`, `succeeded`, `failed`, `blocked`, `canceled`, `needs_attention`, `outcome_unknown`. Workflow terminal states include `succeeded`, `failed`, `canceled`, `needs_attention`, `outcome_unknown`; `running` is intermediate.

**`run.json` is the authoritative workflow state**, not JSONL. It stores manifest identity, timestamps, overall state, individual node results, failure information and recorded host/build environments. The complete [NodeResult implementation](../../CMakeWorkflow.CMake/WorkflowRunner.cs) also holds executable and artifact identity, arguments, timing and outputs. Node `stdout`, `stderr` and output paths may be relative to the run directory; event artifact paths may be absolute. Always validate an artifact path exists before opening it.

A robust JSONL consumer handles arbitrary UTF-8 chunk boundaries, LF/CRLF, a final line without a newline, abrupt disconnect and schema incompatibility. A truncated stream **must not be displayed as success**. `workflow report` returning 0 only means reading succeeded: inspect the saved `status`.

## Interruption, uncertain effects and resumption

A durable admission checkpoint may mark a node `running` before the child process starts. After a crash, there may or may not have been external side effects. For compatible interrupted checkpoints, `workflow resume RUN_DIRECTORY` first quarantines the run as `outcome_unknown` and exits **4** without dispatching new work. It changes only interrupted nodes, does not fabricate process completion timestamps and does not increment `resumeCount`. A second resume on the unknown checkpoint is rejected. A normal resume of `needs_attention` runs still validates graph, manifest and previously successful artifacts; failed/canceled runs cannot be retried generically via resume.

Inspect logs, external state and child processes, then issue an explicit auditable `workflow reconcile` decision with the **current raw checkpoint SHA-256**. Reconciliation does not itself start work; it may prepare a subsequent explicit resume. See [Recovery](recovery.md). JSONL does not serve as a crash-recovery database.

## Local SQLite History

[HistoryStore](../../CMakeWorkflow.History/HistoryStore.cs) indexes finalized run results; raw directories remain the source of truth and the catalog is **rebuildable**. Completed success/failed/canceled workflows are indexed automatically unless disabled. Saved profiler results can be indexed after analysis. Manual import is available:

```sh
CMakeWorkflow history import out/results --recursive --db out/history.sqlite --json
CMakeWorkflow history runs --db out/history.sqlite --limit 50
CMakeWorkflow history show RUN_ID --db out/history.sqlite --json
CMakeWorkflow history metrics --run RUN_ID --db out/history.sqlite --json
CMakeWorkflow history query --metric process_wall_time --db out/history.sqlite --json
```

Default catalog: `CMAKEWORKFLOW_HISTORY_DB` or the host's `LocalApplicationData/CMakeWorkflow/history.sqlite`. `CMAKEWORKFLOW_STATE_DIR` relocates persistent local host state. The host identifier is random and local; it is not an OS machine ID or hostname. History uses transactional, idempotent import and migrations from schema 0→1→2→3→4, with indexer-version tracking for derived data. Moving and reimporting a run may produce a different **local** run ID. Active/partial reports are skipped; mutation and weak-memory reports require a checked completion marker. Do not query the SQLite schema directly from an editor: use the History CLI.

## Comparable measurements versus regressions

`history compare BASELINE_ID CANDIDATE_ID` is **descriptive**: it computes sample counts, median, min/max, mean, population standard deviation and supported deltas. Matching metric names alone are insufficient; comparability checks include units, measurement method, workload arguments, scenario/variant, recorded host identity, compiler/toolchain and environment. Missing, incompatible or unverified data must not acquire invented numerical deltas.

`history detect` uses a **one-sided permutation test on median differences**, not just a percentage threshold:

```sh
CMakeWorkflow history detect BASELINE_ID CANDIDATE_ID --metric process_wall_time --direction higher-worse --min-percent 5 --alpha 0.05 --db out/history.sqlite --json
```

It requires a declared worse direction and practical effect, successful/comparable source runs and at least **10 distinct individual samples per run** in each selected variant/scenario. Small sample sets use exact permutations; larger ones use 9,999 deterministic Monte Carlo permutations. Holm correction applies across the selected comparison family; `--variant` and `--scenario` narrow that family. `regression` requires both adjusted p-value ≤ alpha and a median effect at least as large as `--min-percent` in the worse direction. `inconclusive`, `unverified` and `insufficient` are **not proof of equivalence**.

Recorded fingerprints cannot establish independent samples or exclude thermals, frequency drift and background interference. Profiled/instrumented numbers should not silently become ordinary timing baselines. See [HistoryDetector](../../CMakeWorkflow.History/HistoryDetector.cs) and [profiling](profiling-and-campaigns.md).
