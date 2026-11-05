# Process execution domain (v1)

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

The process domain does **not** implement automatic resume after uncertain outcomes.
A lost RPC response is not proof that an effect was not performed: reconnect and use
`operations/find` by `clientRequestId`, then inspect/cancel or explicitly reconcile.
Do not resubmit with a different ID to bypass an unknown outcome.

The process domain is for trusted developer workspaces: command execution has the
same privilege as the Host. Clients must only connect to an authorized Host/workspace.
The v1 process report is intentionally separate from CMake `WorkflowReport`.
