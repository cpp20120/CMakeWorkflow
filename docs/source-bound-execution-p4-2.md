# P4.2: Host-bound Lore source execution (strict managed CMake subset)

A `workflow/start` (or `execution/start` with domain `cmake`) request may contain
`sourceSnapshotId`. Admission persists this ID atomically in SQLite, keyed to the
accepted manifest digest and `clientRequestId`. The P4.2 executor **reads the ID
back from SQLite**, not from another RPC, and refuses missing/corrupt Lore content
before results reservation and `AuthorizeDispatch`.

## Capturing on a Host

The Host-local store is per workspace and lives in the user's application-data
folder, not inside the Git worktree. Derive the path from the **canonical absolute
workspace path**. A developer CLI in `CMakeWorkflow.Sources.Tests` exposes the path:

```bash
RID=linux-x64   # or osx-arm64 / win-x64
WORKSPACE="$(realpath /path/to/workspace)"
dotnet run --project CMakeWorkflow.Sources.Tests -c Release -r "$RID" -- \
  store-path "$WORKSPACE"
# Use the printed store path with capture-git or capture-files.
dotnet run --project CMakeWorkflow.Sources.Tests -c Release -r "$RID" -- \
  capture-git '<printed-store-path>' '/path/to/workspace/source'
```

Pass the printed `snapshotId` as `SourceSnapshotId` on `StartWorkflowOptions`.
This first integration is Host-local. Remote capture, upload and source-store API
are out of scope: the Host must already have the Lore blobs in its own store.

## Path mapping and validation

- The original manifest bytes and SHA-256 stay authoritative in the operation
  ledger. Rewriting context paths happens only in an in-memory planner document.
- Every context must explicitly declare `sourceDirectory`, be managed (or leave
  `mode` unspecified), and refer to **one canonical source root in the workspace**.
- Every context's source path is rewritten to
  `<workspace>/.cw/<operationId>/s`.
- Each context has a private build path under `<workspace>/.cw/<operationId>/b`.
  Initial execution rejects an existing nonempty build directory.
- At least `CMakeLists.txt` at snapshot root is required. Every Lore blob is
  hash/length-verified by the storage adapter during materialization. An
  accepted pre-dispatch retry may reuse an existing materialization only after
  checking every file's exact hash and detecting extra/missing files.
- Buildless `profile_snapshot`, explicit node `workingDirectory`, path-based
  artifact inputs and manifest-rooted artifact outputs are rejected in P4.2;
  they need more complete source/path remapping.
- All existing unsourced workflows use the old planner/executor unchanged.

**This is snapshot-controlled compilation, not a sandbox.** The manifest and
CMake files can still run arbitrary scripts, access absolute external paths,
network resources or mutate the isolated source. Full hermetic builds require a
separate filesystem/process isolation design. A snapshot contains regular files
only; executability, symlinks and `.git` metadata are not restored by Lore v1.

## Recovery boundary

The runner writes `sourceSnapshotId` to `run.json`. The Host checks that binding
against the accepted operation before dispatch and again before claiming an
operator-admitted resume. Recovery checks the Lore manifest and blobs, the
materialized source tree, and the retained CMake build and artifact observations.
Completed nodes are skipped. Checkpoints created before source provenance was
recorded cannot resume source-bound execution.

Operation overview exposes the accepted source identity. History imports the
identity from the terminal report. Existing History databases migrate to schema
5 when opened for writing, for example by `history import`.

## Tests

The CI matrix runs the Lore E2E and then the RID-specific Host E2E:

```bash
dotnet restore CMakeWorkflow.Host.Tests/CMakeWorkflow.Host.Tests.csproj -r linux-x64
dotnet build CMakeWorkflow.Host.Tests/CMakeWorkflow.Host.Tests.csproj -c Release -r linux-x64 -warnaserror
dotnet run --project CMakeWorkflow.Host.Tests -c Release -r linux-x64 --no-build -- --source-binding-tests
```

The E2E accepts a snapshot, modifies the live source before dispatch, executes a
real C++ `configure → build → run` graph, asserts the compiled captured output, verifies a
fresh tree, and tests a missing Lore snapshot refusing dispatch. Windows and
macOS run the same test with their RIDs.
