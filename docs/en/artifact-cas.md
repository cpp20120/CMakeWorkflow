# Artifact storage and action reuse

Use a local directory for the store. A single client can import finalized operations from multiple Hosts into the
same store over existing local or SSH transport. Artifacts with identical SHA-256 share one blob. For a running local
Host, take its socket, workspace and a finalized operation ID:

```sh
CMakeWorkflow remote stash --transport local --endpoint HOST_SOCKET --workspace HOST_WORKSPACE --operation OP_ID --store out/artifact-cas
CMakeWorkflow cas out/artifact-cas show OP_ID
CMakeWorkflow cas out/artifact-cas fetch OP_ID report out/archived-run.json
```

To retrieve from another Host, use the SSH connection flags from `CMakeWorkflow remote help` and keep the same
`--store`. The returned reference records the workspace, operation, recorded Host identity when available, logical paths,
roles, digests and sizes. `logicalBytes` counts each reference. `distinctBytes` counts each blob referenced by that operation
once. Only files available in the finalized operation catalog are eligible, including result-local saved binaries,
file-based profile traces and declared result outputs. Use declared outputs to archive files such as object files.

Import pins the operation. Readers verify size and digest before publishing a new destination. Store operations share
an exclusive lease, including downloads, publication and GC. Concurrent commands using a busy store can be rerun after
the lease is released. Keep the store in a directory controlled by the local user or service.

```sh
CMakeWorkflow cas out/artifact-cas unpin OP_ID
CMakeWorkflow cas out/artifact-cas gc
CMakeWorkflow cas out/artifact-cas gc --apply
```

`gc` reports unreachable blobs by default. Applying it deletes blobs that have neither a pinned operation reference
nor an action reference. Unpinned operation metadata remains available for provenance. Import accepts finalized operations
and opens no live run for transfer. In-progress imports and action transfers hold the store lease.

## Reuse a declared action output

Set `CMAKEWORKFLOW_ARTIFACT_CAS` to an absolute store path on the execution machine, in the CLI environment or in the
Host service environment. A compatible Host advertises `workflow/actionCache`. Use `"actionCache": true` on a schema-v2
`run` node with declared inputs and one fresh regular
data-file output. Its output uses `root: "manifest"`. The action must be deterministic, with every file it reads declared as
an input and all effects confined to that output.

```sh
export CMAKEWORKFLOW_ARTIFACT_CAS="$PWD/out/artifact-cas"
CMakeWorkflow workflow run examples/hello/workflows/action-cache.json --results-dir out/action-cache-demo
mv out/hello/action-message.txt out/first-action-message.txt
CMakeWorkflow workflow run examples/hello/workflows/action-cache.json --results-dir out/action-cache-demo
cmp out/first-action-message.txt out/hello/action-message.txt
```

The executable, argv, working directory, declared input digests, output path, effective environment, Host identity and
available toolchain identity contribute to the action digest. The second run records `actionCacheHit: true`, materializes
the verified output, and has no process timing sample for that node. Its build prerequisite still runs through CMake.
Action reuse is scoped to the same Host and paths. Corrupt action evidence causes the node to fail. To retire an action
reference, run `CMakeWorkflow cas STORE drop-action ACTION_DIGEST`, then inspect the GC plan.

## Inspect runs across hosts

```sh
CMakeWorkflow history host-summary RUN_A RUN_B --db out/history.sqlite
```

This prints the observed values for each requested run. `buildSeconds` sums measured build-node durations, which may overlap
with parallel builds. The hit-rate column identifies the counter scope. Result bytes are the sizes of distinct referenced
files observed at import inside the results directory, excluding `run.json`. They describe the result tree before CAS
deduplication. Reimport existing results to populate the new measurements where the evidence is available.
Process executions record `processResources` in `run.json`. History imports `peak_rss_bytes` and
`cpu_time`. The sampler requests a 50 ms interval. Linux observes the launched process and descendants
visible through procfs, including CPU accumulated by waited-for children. Windows and macOS observe
the direct launched process through `System.Diagnostics.Process`. Sampling may miss short processes,
brief peaks, departed/reparented descendants, or processes outside the traversal limit of 4096 processes.
These are sampled estimates with collection overhead.

The host summary reports the largest per-node sampled RSS and the sum of per-node sampled CPU time.
Parallel workflow nodes can overlap, so the largest node value does not establish a simultaneous workflow peak.
`resourceMethod` includes the collector, scope and interval. Mixed scopes/methods produce unavailable aggregate
resource values. Old reports and cache hits without execution retain null values.

Memory-stall cycles require an explicit Linux perf counters capture. Use `profile run --memory-stall-event EVENT`
or a profile node with `options.memoryStallEvent`. Supported events are `cycle_activity.stalls_mem_any`
(Intel outstanding-load execution stalls) and `ex_no_retire.load_not_complete` (AMD cycles without retirement
while the oldest operation waits for load data). Each accepts an optional `:u` or `:k` modifier.
Availability and permissions depend on the host. An unsupported requested event fails the capture.
Uncounted counters remain absent.

`memoryStalls` sums one event, privilege domain, variant and workload. `memoryStallEvent` retains its exact name.
Mixed events or workloads produce an unavailable total. Compare hosts using the same event and workload and
review their CPU identities. Intel and AMD event meanings differ. Cache misses remain separate metrics.

```sh
CMakeWorkflow profile run --build-dir out/hello --target workflow_demo --backend perf --mode counters \
  --memory-stall-event cycle_activity.stalls_mem_any:u --results-dir out/memory-profile
```

A Linux profile node can select the AMD event through the existing options object.

```json
{
  "id": "memory",
  "kind": "profile",
  "from": "build",
  "dependsOn": ["build"],
  "options": {
    "backend": "perf",
    "mode": "counters",
    "memoryStallEvent": "ex_no_retire.load_not_complete:u"
  }
}
```

Event definitions are documented by [Intel](https://perfmon-events.intel.com/platforms/icelakex/core-events/core/)
and the [Linux perf AMD Zen 4 event table](https://github.com/torvalds/linux/blob/v6.12/tools/perf/pmu-events/arch/x86/amdzen4/core.json).
