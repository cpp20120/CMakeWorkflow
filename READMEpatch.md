# CMakeWorkflow Host profiling patches — 2026-11-01

Target repository: https://github.com/cpp20120/CMakeWorkflow
Base commit: `218912dcff62b5809813cb847ee654fd3a3bf22c` (`main` on 2026-10-31).

This is an **unmerged, unbuilt source patch series**. The GitHub integration for this session can read the repository
but returned HTTP 403 when creating a branch. The container has no .NET SDK and no GitHub network access, so neither
compilation nor CI was run. Patch format was checked with `git apply --stat`, and all five patches passed sequential
`git apply --check` / `git apply` on synthetic preimages reconstructed from the patch hunks. This **does not** replace
`git apply --check` against the real checkout or a .NET build.

Apply in order on a new branch from the base commit:

```bash
git switch -c feature/host-profile-workflows 218912dcff62b5809813cb847ee654fd3a3bf22c
bash apply_patches.sh

dotnet build CMakeWorkflow.slnx -c Release
# Run the project test commands in README / .github/workflows/ci.yml
```

Patches:

1. `01-host-profile-execution.patch`: pass the existing `ProfileWorkflowExecutor` to the durable Host runner for initial
   dispatch and resume. No second scheduler.
2. `02-profile-builder.patch`: extract the profile workflow DAG builder to `ProfileWorkflowBuilder`, called by the
   existing CLI and now available to the Host. Retains separate uninstrumented timing and instrumented capture nodes.
3. `03-profile-prepare-rpc.patch`: add typed `profile/prepare` producing a stable manifest path/hash. Workspace root
   checks, request ID conflict detection, deterministic serialization, source-generated JSON DTOs, wire tests.
4. `04-profile-analyze-rpc.patch`: add typed `profile/analyze` for re-running saved profile capture analysis. It returns
   the `analysis.json` path. Before analysis, check workspace containment of inputs and known generated outputs, and
   bound metadata file counts. Analysis is connection-scoped and intentionally not represented as a durable benchmark
   operation.
5. `05-profile-rpc-docs.patch`: English and Russian Host API documentation.

Recommended GUI flow after applying:

`profile/prepare` → `workflow/plan` (preview) → `workflow/start` (stable `clientRequestId`, resulting manifest/hash,
unique run directory) → `operations/get/events/cancel` → `runs/get` → `profile/analyze`.

`profile/prepare` does not run anything. Its `requestId` is 32 hex characters and can be reused only with semantically
identical parameters. `workflow/start` remains the durable admission boundary. Always use a stable `clientRequestId` on
retry: response loss must result in `operations/find`, not a new launch.

Still to implement (not part of this patch series): typed RPC for profile replay, DagFlow fuzz/mutation/weak-memory
campaigns and target build/run/sequence. The existing CLI is intentionally unchanged as an agent/automation interface.
