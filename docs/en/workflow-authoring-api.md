# Workflow authoring and provisioning API (P3)

P3 adds a read-only `workflow/preflight` Host RPC and typed public .NET SDK
entry points. The existing `workflow/plan`, recipe preparation, durable start,
Doctor and Fix wire protocols remain backwards-compatible.

## Explicit lifecycle

1. **Prepare** with `client.Recipes.Prepare*Async` (or `client.Benchmarks` /
   `client.Profiles` / `client.Targets`). Preparation may persist an immutable
   manifest and its request binding, but never starts the workflow.
2. **Preflight** with `client.Workflows.PreflightAsync(new WorkflowPreflightOptions(
   manifest, manifestSha256, jobs, resultsDirectory))`, or pass a `PreparedRecipe`/
   `PreparedBenchmark`/`PreparedWorkflow` directly to an overload. The Host binds the
   requested SHA-256 to one in-memory manifest byte snapshot, validates paths,
   loads the DAG, returns node dependencies, resource constraints, diagnostics,
   observed build context and `Valid`. It does not configure,
   build, create a results directory, authorize execution, or install tools.
3. **Review** the returned nodes/diagnostics. If `Valid`, call
   `preflight.CreateStartOptions(clientRequestId)` to construct an explicit
   start request. This method is pure and does not dispatch.
4. **Start** with `client.Workflows.StartAsync(options)`. The Host still verifies
   and durably binds the manifest digest at admission. Preflight is not a
   lease, transaction, or promise that the filesystem remains unchanged.
   Persist the caller-generated `clientRequestId` before the RPC; on a lost
   response, reconnect and use `client.Operations.FindAsync(id)`, not a new ID.

Preflight does not turn an invalid graph into a successful result: the `Valid`
field reflects planner Error diagnostics. Malformed documents, unsafe paths,
missing inputs, bad hash and other failures are stable Host RPC refusals.

## Provisioning API

`client.Provisioning.DoctorAsync` probes tools without installing them.
`PlanFixAsync` reviews a tool plan; its `PlanId` is time-limited and bound to
that live Host connection. `ApplyFixAsync` explicitly requests one attempt
against that plan. It may install packages or modify the Host; it never
implicitly retries after cancellation/disconnect. PlanFix has no effect on
execution state, and an expired/foreign/superseded plan cannot be applied.

For tooling DTOs the Client uses independent `Host*` records and AOT-safe
string-enum converters instead of referencing Host or Tooling assemblies.
The SDK surfaces stable application refusal codes through
`CMakeWorkflowRpcRefusalException.Code` when Host supplies structured
error data. Other transport/protocol failures remain their original exceptions.

## Compatibility and scope

- All RPC additions are additive; `workflow/start`, `workflow/plan`,
  `tooling/doctor`, `tooling/planFix`, `tooling/applyFix` keep their wire names.
- Preflight snapshots the *manifest bytes*; observations of external build
  trees and tool availability can become stale. Start rechecks its own
  admission invariants; a successful preflight is **not** durable authorization.
- Paths are Host-side paths, including over SSH. Only initialized workspace
  paths are accepted by `workflow/preflight`.
- Progress notifications for Doctor/Fix are already supported by Host RPC;
  this P3 SDK slice exposes request/response typed operations, not a new
  notification stream. SDK event subscription is a separate enhancement.
