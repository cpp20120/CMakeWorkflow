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

## P2b: durable Host child coordination (experimental)

`CMakeWorkflow.Pipelines.Coordinator` connects the SQLite ledger to the existing
`CMakeWorkflow.Client` Host RPC facade (`workflow/preflight`, `workflow/start`,
`operations/find`, `operations/overview`). It is **not** installed as a new
Host RPC endpoint, CLI command or autonomous scheduler yet. Use a persistent,
workspace-bound Host connection; disposing a private launched Host may interrupt
its child operations.

The ledger schema is now **v2**. Opening a v1 database additively creates the
`pipeline_child_launches` table. Each new coordinator-authorized reservation
atomically binds immutable Host workspace path, absolute manifest path, exact
SHA-256, jobs, result directory and the deterministic request ID. Legacy
three-argument `ReserveWorkflow` remains readable but **cannot be dispatched
or automatically rebound by the P2b coordinator** because it has no pinned
Host launch specification. Stage IDs of any legal size produce bounded
Host request IDs.

1. `StartReadyAsync` first performs read-only Host preflight, then commits the
   pipeline-wide CAS reservation and pinned child launch, then sends **one**
   `workflow/start` RPC. It checks the returned request ID, domain and manifest
   hash before binding the operation ID. A failure at any point after reservation
   leaves `dispatch_intent` and never triggers automatic retries.
2. `RecoverAsync` on `dispatch_intent` uses only `operations/find` with the
   original persisted request ID and validates the pinned Host identity. A
   lookup error or not-found is **not** authority to start another operation.
   This call needs exclusive owner/quiescence discipline when a dispatch might
   still be in flight; it never claims or changes that ownership itself.
3. `RecoverAsync` on `waiting_external` reads `operations/overview`, and only
   a Host-verified terminal report SHA-256 can transition to `succeeded` or
   `failed` (including canceled as a non-success). Unfinalized terminal-looking
   observations do **not** prove a stage finished.

No automatic polling, external GitHub API, artifact transfer, deployment,
child cancellation, process lease acquisition or general pipeline scheduling
is included. In particular, this patch does **not** enforce the cross-process
Host ownership lease required for fully autonomous recovery: the caller must
manage that before invoking concurrent dispatch/recovery. If a reserved child
was never dispatched and read-only lookup cannot find it, the stage intentionally
stays unresolved; operator reconciliation requires verified prior-owner
quiescence. The P3 GitHub adapter is described below.

## P3: read-only GitHub Actions attach/watch (experimental)

`CMakeWorkflow.Pipelines.GitHub` is a separate **read-only adapter**. It does
not submit, rerun, cancel, approve or download anything. The P1 manifest
supports `github.actions` `mode: "attach"` with a pinned `repository`, workflow
YAML filename, `runId`, `runAttempt` and `expectedHeadSha`. P3 turns that
pre-existing manifest into verified provider observation, using the existing
P2a `waiting_external` stage state; the SQLite schema remains **v2**.

1. `GitHubActionsStageWatcher.AttachReadyAsync(operationId, stageId)` checks
   prerequisites, queries the **specific attempt** from GitHub, checks the exact
   repository / run ID / attempt / workflow path / commit SHA, and then stores
   the immutable `github:` binding via a ledger CAS. Errors do not attach.
2. `ObserveAsync(operationId, stageId)` reconstructs the pinned identity from
   the **immutable admitted manifest** and compares it with the persisted
   binding before another provider GET. Nonterminal observations leave the
   ledger unchanged. A verified `completed` + `success` becomes `succeeded`;
   any other recognized terminal conclusion becomes `failed`. Unknown or
   inconsistent provider values are refused, not interpreted as success.
3. Each terminal stage journals an evidence reference to the exact repository,
   run, attempt and SHA-256 of the GitHub JSON response received. This SHA-256
   is a local audit fingerprint, **not** a GitHub-signed attestation. A
   committed terminal result is immutable even if GitHub later reruns the run.

`GitHubActionsHttpReader(HttpClient, token)` uses only
`GET /repos/{owner}/{repo}/actions/runs/{run_id}/attempts/{attempt_number}`;
`HttpClient` and any read-only token are borrowed from the caller. For private
repositories, supply a token with Actions **read** access. Public run reads
can be anonymous, subject to rate limits. Responses are capped at 1 MiB;
403/404/429, cancellation, timeouts, malformed responses and identity changes
remain errors and **never** trigger a new external run or automatic retry.

```csharp
using CMakeWorkflow.Pipelines.GitHub;
using CMakeWorkflow.Pipelines.Persistence;

using var ledger = new PipelineLedger("out/pipelines.sqlite");
using var http = new HttpClient();
var watcher = new GitHubActionsStageWatcher(ledger,
    new GitHubActionsHttpReader(http, token: null));
// The stage must already exist in an accepted PipelineDocument.
var attached = await watcher.AttachReadyAsync(operationId, "ci");
var latest = await watcher.ObserveAsync(attached.OperationId, "ci");
// Or await watcher.WatchUntilTerminalAsync(attached.OperationId, "ci")
// to poll every 30 seconds until terminal (caller-controlled cancellation).
```

This is explicit observation, **not** an autonomous poller. The caller is
responsible for credentials, request scheduling, ledger/Host ownership and
reconnections. No new Host RPC, CLI command, container runtime, artifact
federation or `workflow_dispatch` support is included in P3.

## P4: durable GitHub Actions workflow_dispatch (experimental)

`GitHubActionsDispatchCoordinator` supports `github.actions` stages with
`mode: "dispatch"`, `repository`, `workflow`, `expectedHeadSha`, `ref` and
optional `inputs`. `runId` and `runAttempt` must be omitted (or zero) for a
dispatch; attach mode is unchanged. `ref` must be an explicit branch/tag,
e.g. `refs/heads/main`. The expected SHA must be a full 40-character commit ID.
No token is serialized into a pipeline manifest or SQLite database.

The target GitHub workflow (present on the repository's default branch) must
opt in to correlation by declaring `cmwf_request_id` and setting `run-name`:

```yaml
name: CI
run-name: cmwf-${{ inputs.cmwf_request_id }}
on:
  workflow_dispatch:
    inputs:
      cmwf_request_id:
        required: true
        type: string
```

Example admitted stage:

```json
{
  "id": "ci",
  "kind": "github.actions",
  "githubActions": {
    "mode": "dispatch",
    "repository": "cpp20120/CMakeWorkflow",
    "workflow": "ci.yml",
    "expectedHeadSha": "0123456789abcdef0123456789abcdef01234567",
    "ref": "refs/heads/feature/pipelines-v1",
    "inputs": {}
  }
}
```

1. `DispatchReadyAsync` performs the prerequisite CAS and **commits the
   dispatch_intent** with a deterministic `cmwf_request_id` before any POST.
   It then makes **one** GitHub `workflow_dispatch` POST with
   `return_run_details=true` and parses its 200 response. When a run ID is
   returned, it immediately binds attempt 1 via CAS, before another GET.
2. **Never call dispatch again** after the intent commits. A 204, timeout,
   4xx/5xx, cancellation, malformed or missing receipt, crash, or lost reply
   leaves an unresolved intent. GitHub does not provide request-key
   deduplication; we do not claim exactly-once remote execution.
3. `ReconcileKnownRunAsync(operationId,stageId,candidateRunId)` is **read-only
   to GitHub**: an operator supplies a candidate run ID, and the adapter
   verifies repository, workflow, attempt 1, SHA, `workflow_dispatch` event,
   and exact `run-name` correlation marker before the ledger binds it. An
   absent candidate **never** authorizes another POST.
4. `ObserveAsync` reads only the pinned attempt. A provider GET verifies
   all fields, including correlation, before terminal outcome is committed.
   An unknown completion state remains a failure to observe, not success.
   `WatchUntilTerminalAsync` is an optional caller-controlled polling loop.

The caller supplies a GitHub Actions **write** token to
`GitHubActionsHttpDispatcher` and at least read permission to
`GitHubActionsHttpReader`. Both borrow `HttpClient`, neither logs tokens.
This P4 slice does not register new Host RPC or CLI endpoints, start a
background daemon, select host leases, retry uncertain requests, or transfer
artifacts. A successful `200` response pins an **unverified receipt**; it is
not considered successful execution until the subsequent validated GET.
If the branch moves between approval and dispatch, the returned commit SHA
may mismatch: observation/reconciliation must refuse to misattribute the run.
