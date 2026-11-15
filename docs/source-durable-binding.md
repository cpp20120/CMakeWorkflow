# Durable source binding: P4.0/P4.1

This patch starts from the **merged `main`**, not from the Windows provisioning
or earlier source-snapshot feature branch.

## What is implemented

- CI restores the RID-specific `LoreVcs` runtime and executes the real
  `CMakeWorkflow.Sources.Tests self-test` on Linux x64, Windows x64 and macOS ARM64.
- `WorkflowStartRequest.SourceSnapshotId` is an optional 64-character SHA-256
  source identity; the domain-qualified `ExecutionStartRequest` forwards it
  for `cmake` operations; process-domain requests explicitly reject this field.
- A new SQLite `operation_source_snapshots` table stores the source identity in
  the **same transaction** as immutable workflow acceptance. No existing
  accepted-workflow column is repurposed; `snapshot BLOB` remains the exact
  workflow-manifest bytes.
- The request digest remains *byte-for-byte unchanged* for legacy requests with
  no source identity. Source-bound requests extend it with a domain separator and
  uppercase canonical SHA-256. Duplicate client IDs with a different (or missing)
  source snapshot are rejected as `request_id_conflict`.
- Read-only inspection tolerates a legacy ledger without the new table.
- Persistence tests cover commit/restart/idempotency/conflicting source IDs.

## Deliberate fail-closed boundary

`DurableExecutionCoordinator.Start` refuses non-null `SourceSnapshotId` with
`source_snapshot_execution_not_enabled`. This is **intentional**, not a completed
execution feature: binding a snapshot in SQLite without materializing and
actually configuring CMake against it would give false provenance.

P4.2 must implement a pre-authorization source materialization step, allocate
an operation-specific *fresh* CMake build tree, bind all workflow source paths
and source-related working directories to that materialized tree, and persist
source identity in the runner report/checkpoint. P4.3 must recover from those
exact bytes and refuse missing or changed snapshots. Existing non-snapshot
workflows remain compatible.

## Checks

```bash
dotnet build CMakeWorkflow.slnx -c Release -p:TreatWarningsAsErrors=true
dotnet CMakeWorkflow.Host.Tests/bin/Release/net10.0/CMakeWorkflow.Host.Tests.dll --durable-persistence-tests
dotnet restore CMakeWorkflow.Sources.Tests/CMakeWorkflow.Sources.Tests.csproj -r linux-x64
dotnet build CMakeWorkflow.Sources.Tests/CMakeWorkflow.Sources.Tests.csproj -c Release -r linux-x64 -warnaserror
dotnet run --project CMakeWorkflow.Sources.Tests -c Release -r linux-x64 --no-build -- self-test
```
