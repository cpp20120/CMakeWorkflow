# Pipelines v1 — planning foundation (experimental)

The independent `CMakeWorkflow.Pipelines` assembly introduces a **pure, read-only planner**, not an execution feature.
It does not extend the stable
`CMakeWorkflow.Client` / `CMakeWorkflow.Contracts` SDK package boundary. There is **no pipeline CLI, Host RPC, GitHub
API call, persistent pipeline coordinator, or deployment adapter** in this patch.

Its initial goal is to validate the cross-domain graph before implementing durable orchestration. The two accepted stage
kinds are:

- `workflow`: references an existing local CMakeWorkflow manifest; this patch only checks its workspace-relative lexical
  path.
- `github.actions`: `mode: "attach"` references one **already existing**
  GitHub Actions run and attempt. No dispatch, retry, cancel or API lookup.

Example (the GitHub identity values are illustrative, not a runnable CI run):

```json
{
  "schemaVersion": 1,
  "name": "release",
  "stages": [
    {
      "id": "debug",
      "kind": "workflow",
      "workflow": { "manifest": "examples/hello/workflow.json" }
    },
    {
      "id": "ci",
      "kind": "github.actions",
      "dependsOn": ["debug"],
      "githubActions": {
        "mode": "attach",
        "repository": "cpp20120/CMakeWorkflow",
        "workflow": "ci.yml",
        "runId": 123456,
        "runAttempt": 1,
        "expectedHeadSha": "0123456789abcdef0123456789abcdef01234567"
      }
    }
  ]
}
```

Use `PipelineManifest.ParseJson(json)` and
`PipelinePlanner.Plan(document)`. The planner validates the entire finite graph (1–256 stages), case-sensitive stage
IDs, unique prerequisites, forward dependencies, cycles, stage option exclusivity, and GitHub attempt / commit binding
fields. It returns a detached, dependency-first plan. Deserialization rejects missing required and unknown JSON fields.

**Non-guarantees:** static validation does not read either manifest, verify a GitHub run, authorize a workspace path,
validate symlinks or credentials, capture artifacts, or guarantee idempotent external operations. Those checks belong to
a later Host-owned admission/execution adapter. Particularly, a GitHub run ID alone cannot establish commit or workflow
identity; the future adapter must verify all binding fields with GitHub.

P2a persists stage state; Host-owned dispatch and verified external watch remain future work.

## P2a: durable admission and stage journal (experimental)

`CMakeWorkflow.Pipelines.Persistence` is a separate assembly. The P1 parser and
planner retain **no SQLite dependency**. This layer is a local SQLite ledger,
not yet a Host service or coordinator, and is deliberately not published as part
of the stable Client/Contracts SDK. It is not used by CLI or agent tools yet.

Use `PipelineLedger(databasePath)` with a **dedicated database file**. The
ledger enables foreign keys, WAL and `synchronous=FULL`, and uses single
transactions for immutable admission, stage mutation, revision increments and
event writes. The schema is versioned (`user_version = 1`) and newer versions
are refused. Callers must not mutate a ledger instance concurrently; use
independent instances/connections for concurrent writers.

- `Accept(clientRequestId, document)` validates a frozen JSON snapshot and
  atomically records a new `operationId` plus every `pending` stage. Repeating
  the same request ID with identical serialized bytes returns that *same*
  operation, including its current state. A different manifest under the same
  request ID is refused. The original JSON and its SHA-256 remain retrievable.
- `Find(clientRequestId)` and `Get(operationId)` are read-only and support
  lost-reply lookup and restart recovery. `Events(operationId, afterRevision)`
  returns a cursor-based, append-only transition journal.
- Every stage transition takes an **expected operation-wide revision** and
  commits its stage state, bindings and event in one transaction. Stale revisions
  and invalid transitions are refused, including across two SQLite connections.
- `ReserveWorkflow` persists a deterministic `childRequestId` with a
  `dispatch_intent` **before** a future Host may submit a child. `BindChild`
  records the child operation ID and moves to `waiting_external`.
- `WatchExistingGitHub` persists a pinned, immutable GitHub run/attempt
  reference and moves to `waiting_external`. It does **not** call GitHub, verify
  the identity or start another workflow. A later observer must verify all
  pinned fields against authoritative GitHub data.
- `RecordOutcome` is a **trusted-adapter journal primitive**, not evidence
  verification: it requires a previously persisted binding and a bounded
  evidence reference. It may record `succeeded` or `failed`, but the later Host
  adapter must verify child reports or provider state before calling it.
- `QuarantineInterruptedDispatch` can move a previously reserved but
  unacknowledged dispatch to `outcome_unknown` only after an explicit
  prior-owner-quiescence assertion. The intent and child request ID are
  preserved. `outcome_unknown` is **not retryable**; dependent stages remain
  blocked. There is no automatic redispatch at restart.

Example for a future trusted Host adapter:

```csharp
using CMakeWorkflow.Pipelines;
using CMakeWorkflow.Pipelines.Persistence;

var document = PipelineManifest.ParseJson(File.ReadAllText("release.pipeline.json"));
using var ledger = new PipelineLedger("out/pipelines.sqlite");
var accepted = ledger.Accept("client-request-123", document);
var reserved = ledger.ReserveWorkflow(accepted.OperationId, "debug", accepted.Revision);
// Persisted before any child StartAsync. Lookup by the stored ChildRequestId
// on reconnect instead of resubmitting with a new request ID.
var childRequestId = reserved.Stages.Single(s => s.Id == "debug").ChildRequestId;
```

**Non-guarantees:** a local database is not an authorization boundary, and the
ledger does not acquire cross-process Host leases, verify provider responses,
synchronize child outcomes, cancel remote work, or recover an unknown effect.
The calling Host must own its execution/dispatch lease and validate external
evidence. This patch intentionally does not support workflow dispatch, GitHub
API calls, automatic pollers, deployment or approvals.

Next: P2b binds this ledger to Host-owned child operations with an explicit
reconciliation and ownership protocol. The external GitHub watch adapter
comes after that; it must not turn unknown outcomes into automatic retries.
