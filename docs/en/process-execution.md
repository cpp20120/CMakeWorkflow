# Process execution domain (v1)

[Русский](../ru/process-execution.md) · [Architecture](architecture.md)

`execution/start` accepts `{domain:"process",clientRequestId,manifest,manifestSha256,jobs,resultsDirectory}`.
All filesystem paths are **Host** paths inside the initialized workspace. The manifest is immutable once admitted.
`workflow/start` and `operations/report` retain their existing CMake-specific contracts.

Example manifest:

```json
{
  "schemaVersion": 1,
  "domain": "process",
  "nodes": [
    { "id": "prepare", "executable": "dotnet", "arguments": ["--info"] },
    { "id": "check", "executable": "dotnet", "arguments": ["--list-sdks"],
      "dependencies": ["prepare"], "resources": ["sdk"] }
  ]
}
```

The Host persists immutable admission in the same SQLite store as CMake operations.
A process-domain runner uses the neutral `ExecutionScheduler`, OS process backend,
write-ahead `run.json` and domain-specific checkpoint codec. `operations/find/get/cancel/events` and verified `operations/artifacts/list/readChunk`
work for both domains. Read verified process results through `execution/report` instead
of the existing CMake-specific `operations/report`.

The process domain does **not** automatically resume after uncertain outcomes.
A lost RPC response is not proof that an effect was not performed: reconnect and use
`operations/find` by `clientRequestId`, then inspect/cancel or explicitly reconcile.
Do not resubmit with a different ID to bypass an unknown outcome.

The process domain is for trusted developer workspaces: command execution has the
same privilege as the Host. Clients must only connect to an authorized Host/workspace.
The v1 process report is intentionally separate from CMake `WorkflowReport`.

## Explicit recovery and resume

Process operations use the existing SQLite reconciliation, resume admission and
single resume-attempt protocol. They keep the original operation ID and immutable
manifest snapshot; editing the original manifest does not change a resumed operation.
Resume currently uses one workflow worker (`jobs = 1`).

After stopping or waiting for every external process, use the existing RPCs (or the
corresponding operation-handle SDK methods):

1. `workflow/recoveryInspect` reads the durable operation revision and binding.
2. `workflow/reconcile` with `decision: "needs_attention"`, a nonempty operator note,
   the inspected revision and `confirmedQuiescent: true` records the decision.
3. `workflow/resume` with revision **1** and explicit quiescence confirmation validates
   the process checkpoint and prepares its recovery audit, then admits resume at revision **2**.
   An identical admission retry uses revision 2. Admission starts no process.
4. `workflow/resumeExecute` with revision **2** and explicit quiescence confirmation
   schedules the Host-owned attempt. Before changing runner state it acquires the
   operation and run leases, rechecks the checkpoint bytes and claims the existing
   one-shot SQLite resume attempt. Disconnecting the RPC client does not cancel it.
5. Find/get the original operation after a lost response; read finalized process
   results through `execution/report`. Cancellation remains `operations/cancel`,
   with the existing, separate cancellation-intent revision.

The SDK requires an explicit recovery revision for `ExecuteResumeAsync`. The current value **2** comes from the
persisted `0 -> 1 -> 2` reconciliation/admission transitions; it is not a retry counter or a default the SDK can infer.
After reconciliation to `needs_attention` and verification that external work has stopped, pass the admission result:

```csharp
RecoverySnapshot reconciled = await operation.InspectRecoveryAsync(ct);
RecoverySnapshot admitted = await operation.AdmitResumeAsync(
    reconciled.Revision, confirmedQuiescent: true, ct: ct);
RecoverySnapshot scheduled = await operation.ExecuteResumeAsync(admitted.Revision, ct);
```

`scheduled` acknowledges scheduling, not completion. A stale revision is refused without dispatch; an interrupted
RPC may still have scheduled the Host-owned attempt. Reconnect and inspect before deciding how to proceed.

An operation-level `needs_attention` decision **does not resolve a running node**.
Resume rejects every `running` or `outcome_unknown` node. It executes only
`pending`, `blocked` or `needs_attention` nodes that have no prior execution
metadata and were also never admitted in the last SQLite-bound dispatch checkpoint.
Succeeded nodes retain their logs and results; failed/canceled nodes also retain
their terminal results and are never retried. Failed prerequisites still block
dependent nodes. A run whose nodes all completed durably can finalize without
executing them again, even if the Host died before writing the terminal run report.

The v1 report adds optional dependencies, `recoveryPhase`, `resumeCount` and an audit
containing the operator note, confirmation time, and exact pre-recovery and bound
dispatch checkpoint bytes. SQLite retains the exact process dispatch bytes next
to their existing hash. Resume checks snapshot identity, node IDs/order, dependencies,
eligible-node history, preserved terminal results, workspace paths and the source
binding. The source-generated JSON format and the CMake report schema remain separate.

Previously stored v1 reports remain readable. Older operations without retained
dispatch bytes cannot be newly admitted for process resume: missing evidence is not
proof that a process never ran. No per-node override or automatic replay of uncertain
effects is provided. Inspect external outputs and handle unresolved work explicitly;
this API cannot convert an uncertain node into a retryable one.

The entire resumed dispatch batch is checkpointed and confirmed through
`ConfirmRunnerCheckpoint` before any child starts. Resume appends events with increasing
sequence numbers and uses new `.resume-1` log names with create-new semantics. Existing
logs are never truncated. A crash **before** claim permits the same explicit admitted
attempt; a crash **after** claim consumes that attempt, even if no process started.
There is no second resume attempt or automatic claim reset. Leases coordinate cooperating
Hosts; quiescence is an operator assertion, not detection of orphaned processes.
Reports and SQLite are trusted local evidence, not signed attestations against a user
who can rewrite the workspace. Executables and external inputs are not snapshotted.

## Verification

Build with deterministic recovery fault hooks enabled, then run:

```sh
dotnet build CMakeWorkflow.Host.Tests -c Release -p:RecoveryFaultInjection=true -m:1
dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll --process-recovery-tests
```

The dedicated suite currently requires Linux for SIGKILL and symlink checks. It uses
real child processes and local JSON-RPC, including dropped transport replies. Windows
recovery behavior requires separate platform execution. Normal builds contain no
environment-controlled recovery fault hooks.
