# Recovery: unknown outcomes, reconciliation and durability

**[English](recovery.md) · [Русский](../recovery.md) · [Index](README.md)**

`run.json` is the **only execution checkpoint**. There is no second recovery database, automatic retry after an unknown outcome or general exactly-once guarantee for arbitrary external side effects.

## Inspect an interrupted run

```sh
CMakeWorkflow workflow resume RUN_DIRECTORY
CMakeWorkflow workflow report RUN_DIRECTORY
```

On a compatible interrupted `running` checkpoint, the first `resume` safely classifies the run as `outcome_unknown` and exits **4**, without dispatching any operation. Repeating `resume` before reconciliation is rejected (exit **1**). Check the actual external outputs, process state and logs first. Stop or await leftover processes: CMakeWorkflow build-tree leases protect participating runners but cannot identify all orphaned processes or prevent an independent Ninja/IDE build.

The human-readable `workflow report` shows `Checkpoint SHA256`. For automation, hash the **raw bytes of `run.json`**, not JSON reserialized by a client. Pass the hash with each decision so a stale operator cannot approve a changed checkpoint.

## Reconcile a node

If the operation really completed, explicitly accept it:

```sh
CMakeWorkflow workflow reconcile RUN_DIRECTORY --node api-workloads \
  --decision succeeded --reason "Verified external outputs and logs" \
  --expected-checkpoint CHECKPOINT_SHA256 --confirm-stopped
```

Or explicitly authorize a retry after assessing duplicate side effects:

```sh
CMakeWorkflow workflow reconcile RUN_DIRECTORY --node api-workloads \
  --decision retry --reason "Old process stopped; partial outputs cleared" \
  --expected-checkpoint CHECKPOINT_SHA256 --confirm-stopped
```

`--confirm-stopped` is **the operator's declaration**, not an automated OS check. The reason is mandatory (up to 4,096 characters) and is stored in an audit trail: do **not** put credentials or secrets in it. `--json` returns the updated report. A reconcile decision itself **does not run anything**.

If several nodes have unknown outcomes, reconcile each separately and use the **new checkpoint hash** after each decision. Once every unknown node is resolved, the run becomes `needs_attention` with `recoveryPhase: reconciled`; only then issue a separate:

```sh
CMakeWorkflow workflow resume RUN_DIRECTORY
```

When a crash happened before any node was admitted, a run-level `--decision retry` **without `--node`** may be available. It cannot override an outstanding unknown node or permit replaying successful work.

## Evidence and manually accepted success

A `succeeded` decision is an explicit assertion, not a reconstructed process result. The CLI does not invent exit code 0, process duration or a completion timestamp: `experimentResult` becomes `ManuallyAccepted`; build verification is likewise `ManuallyAccepted` with null `backendExitCode`. Before accepting, the CLI revalidates the manifest, graph and CMake context under run/tree locks. A build that feeds `run` or `profile` needs an observed executable artifact with identity/hash and File API generation; configure requires compatible recorded configure provenance. Manual acceptance does not synthesize missing provenance or turn arbitrary raw profiler/harness files into verified measurements.

Resume will still reject changed successful artifacts, incompatible trees or modified manifests. If safe verification is impossible, investigate further and decide on a new workflow or an explicit retry after dealing with old effects.

## Audit and replay attempts

`run.json.reconciliations` records decision ID, timestamp, local actor/host, node ID (or null), reason, decision, prior checkpoint SHA-256, prior run/recovery state and a snapshot of the prior node result. The decision and updated state are persisted in **one atomic replacement of `run.json`** under its run lease. A stale `--expected-checkpoint` is rejected without mutation. If a client loses the response after commit, re-read the report rather than blindly retrying the old request.

A permitted retry preserves old logs and gives new attempts names such as `NODE.attempt-2.stdout.log`. The audit stores metadata, **not** immutable copies of every external output; other tools can still change files. The audit is local and unsigned. A user with write access to its directory can modify it, so local actor/host fields are not authentication.

## Checkpoint persistence barriers

Before dispatch, the runner serializes to `run.json.tmp`, calls `FileStream.Flush(true)`, and replaces `run.json` without an intermediate delete or copy/delete fallback. Linux/macOS also fsync the directory and parents; macOS uses `F_FULLFSYNC`. Windows uses `MoveFileExW(REPLACE_EXISTING | WRITE_THROUGH)` after flushing the file. A write, flush, replacement or synchronization error blocks new dispatch. If replacement was visible but final sync failed, durability is not confirmed; the CLI fails or records `outcome_unknown` when it can.

These are meaningful persistence measures, **not an absolute power-loss guarantee**. Filesystem, mount, controller and hardware flush behavior matter; network/FUSE filesystems and virtual disks need separate validation. Logs, CMake outputs and arbitrary external transactions do not inherit `run.json` durability or exactly-once semantics.

References: [FileStream.Flush(Boolean)](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush), [fsync(2)](https://man7.org/linux/man-pages/man2/fsync.2.html), [Apple F_FULLFSYNC](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man2/fsync.2.html), [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw).

## Fault-injection suite

A specialized recovery build enables hooks omitted from normal builds:

```sh
dotnet build tests/CMakeWorkflow.IntegrationTests.csproj -c Release
dotnet publish CMakeWorkflow.csproj -c Release -p:PublishAot=false \
  -p:RecoveryFaultInjection=true --artifacts-path out/recovery-artifacts -o out/recovery-cli
dotnet tests/bin/Release/net10.0/CMakeWorkflow.IntegrationTests.dll \
  out/recovery-cli/CMakeWorkflow --recovery
```

Use `.exe` on Windows; **do not distribute the fault-injection binary** as a production CLI. The suite terminates processes at admission/checkpoint/persistence boundaries and checks counts, recovery decisions, leases and artifact acceptance. `SIGKILL`/`TerminateProcess` and injected write failures do **not** simulate physical power loss; storage fault testing would be necessary to make stronger guarantees.
