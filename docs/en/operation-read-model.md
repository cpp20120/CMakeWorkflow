# Durable read model and history paging

These endpoints are additive to the existing v1 JSON-RPC protocol and do not
modify admission, execution, durable recovery, event journals or stored reports.
The Host workspace is selected during `initialize`; paths refer to that Host's
filesystem, including connections made over SSH.

## `operations/overview`

Request: `{ "operationId": "..." }`.

Response: `OperationOverview` with `schemaVersion: 1`, the existing operation
identity (`workspaceId`, `operationId`, `clientRequestId`, `kind`, `state`,
`manifestSha256`, `manifestBaseDirectory`) and a normalized `nodes` array. CMake terminal views also expose `hostId`
and `environmentFingerprint` when the verified report contains them; the
process domain leaves these fields null rather than inventing provenance.

Each terminal node view contains `id`, `kind`, `status`, `dependencies`,
`startedAt`, `finishedAt`, `exitCode`, `error`, `stdout`, and `stderr`.

- For **nonfinalized** operations, `isFinalized` is false and the response has
  no node evidence, report digest or results directory. Empty `nodes` does
  **not** mean an empty DAG or successful execution. Use `operations/events`
  for live progress and `operations/get` for durable admission/cancel status.
- For **finalized** operations, `isFinalized` is true only after the terminal
  `run.json` has been verified against the Host's finalization and immutable
  manifest binding. `reportSha256` is the verified report digest. The CMake
  and process domains expose the same node DTO; the process DAG's dependencies
  come from its persisted immutable input snapshot.
- The read is side-effect free and does not authorize, resume, retry, or
  dispatch work. Only the actual finalized status is terminal. Failed report
  verification is an error, never a partial success.
- To access domain-specific fields, continue using `operations/report` for
  CMake and `execution/report` for process-domain reports.

The public SDK offers `client.Operations.GetOverviewAsync(operationId)` and
`operation.GetOverviewAsync()`. `WatchEventsAsync` yields replay and live
updates, following the `nextSequence` cursor and finishing once finalization
has been observed and the final journal page has been drained. A disconnected
RPC fails explicitly; the caller must reconnect and resume from its saved
cursor, without automatically redispatching work.

## `history/runsPage`

Request: `{ "databasePath": "...", "limit": 50, "status": null,
"since": null, "until": null, "workflow": null, "variant": null,
"cursor": null }`.

Response: `HistoryRunPage` with `schemaVersion: 1`, `items` (the existing
history run DTOs), and `nextCursor` (null on the last page).

- Pages use the same filtering and workspace path validation as `history/runs`,
  ordered by `started_at DESC, id ASC`. Limit is 1–200.
- The first page captures a SQLite rowid high-water mark. Later imports do not
  appear in the current traversal, even if their timestamps sort earlier.
- Cursor tokens bind to the canonical Host database path and exact filters.
  A cursor cannot be reused with different filters or another workspace.
  They are opaque **pagination state, not authentication tokens**.
- Reimports and deletes of existing rows are not snapshot-isolated between
  pages. Read-only API calls do not create or migrate a history database.
- `history/runs` remains available with its existing response shape.

The SDK offers `client.History.RunsPageAsync(new HistoryRunsPageQuery(...))`.
