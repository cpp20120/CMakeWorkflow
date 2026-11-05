# Host read API: operations and history

The Host remains the authority for durable execution. This read-side API is available
through JSON-RPC and the public `CMakeWorkflow.Client` SDK; consumers must not read
`.cmakeworkflow/operations.sqlite` or `run.json` directly.

## `operations/list`

Request (all fields optional):

```json
{"limit":50,"state":"succeeded","domain":"process","cursor":null}
```

- `limit`: 1–200. The response has `schemaVersion: 1`, `items`, and `nextCursor`.
- `state`: exact effective state (`accepted_not_dispatched`, `dispatch_authorized`,
  `cancel_requested`, `succeeded`, `failed`, `canceled`), or omit for all.
- `domain`: durable domain identity (`workflow` for legacy CMake operations or
  `process`), or omit. Future domain tags use ASCII letters/digits/`_`/`-`.
- `cursor`: opaque keyset cursor returned by the previous page. Do not decode or
  persist as an operation identifier. Cursors are bound to the workspace identity
  and exact filters. A mismatched/invalid cursor is rejected.

Ordering is newest admission first, based on SQLite `rowid`. The first request
captures an admission high-water mark, preventing *new* accepted operations from
appearing in subsequent pages. It is **not** a transactionally consistent snapshot
of mutable states: a concurrent finalization or cancellation may change whether an
existing operation matches a state filter across page requests. For an authoritative
current state call `operations/get` by `operationId`.

List/get/find are read-only. Listing an empty workspace does not create a database,
run, or Host operation. No request can cross the initialized workspace boundary.

## History SDK parity

All existing Host history read/analysis RPCs now have public typed SDK methods:

| RPC | SDK |
| --- | --- |
| `history/runs` | `client.History.RunsAsync(...)` |
| `history/show` | `client.History.ShowAsync(...)` |
| `history/metrics` | `client.History.MetricsAsync(...)` |
| `history/query` | `client.History.QueryAsync(...)` |
| `history/compare` | `client.History.CompareAsync(...)` |
| `history/detect` | `client.History.DetectAsync(...)` |

These methods use SDK-owned source-generated wire DTOs without bringing the History
SQLite implementation into the Client assembly. `DatabasePath` and run directories
refer to the initialized Host workspace, including over SSH; there is no implicit
local path translation. The Host's existing bounds, data validation, workspace
confinement and refusal codes remain authoritative.

```csharp
await using var client = await CMakeWorkflowClient.ConnectAsync(options);

var page = await client.Operations.ListAsync(new OperationQuery(Limit: 50));
var failed = await client.Operations.ListAsync(new OperationQuery(State: "failed"));

var history = await client.History.RunsAsync(
    new HistoryRunsQuery(DatabasePath: "/host/workspace/history.sqlite", Limit: 50));
var detail = await client.History.ShowAsync(
    new HistoryShowQuery("/host/workspace/history.sqlite", history[0].Id));
```

SDK operations operate over a transport connection; losing the connection is not
proof of cancellation or rejection. Reconnect and look up operations by ID or
`clientRequestId` rather than implicitly re-dispatching execution.
