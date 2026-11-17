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
Peak RSS, CPU time and memory stalls are left empty pending dedicated measurement.
