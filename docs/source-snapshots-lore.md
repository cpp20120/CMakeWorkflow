# Source snapshots P2/P3: Lore-backed store and Git-aware selection

The new `CMakeWorkflow.Sources` .NET 10 library separates **CMakeWorkflow source identity**
from **Lore storage addresses**. This branch starts from `main`, not from the Windows
provisioning branch. No changes are made to Host, SDK, CLI, or durable admission yet.

## Contracts

- `ISourceSnapshotStore`: `Capture`, `Inspect`, `Materialize`.
- `LoreSnapshotStore`: disk-backed `LoreVcs 0.10.0` adapter. It initializes only its **private**
  offline `<store>/lore/.lore`, never the user's Git repository. The logical SHA-256
  snapshot manifest lives under `<store>/manifests/`; Lore physical addresses are in
  sibling `.lore.json` bindings. Manifest identity is independent of Lore addresses.
- `GitSourceSelector.Select`: shells out to `git` using `ProcessStartInfo.ArgumentList`,
  reading null-delimited index/other lists. No `git add`, `commit`, `stash` or index mutation.
  Requires the repository **toplevel** as the source path.
- `SourceCaptureService.CaptureGit`: takes selected **working tree** bytes, not indexed blob
  contents, so both staged and unstaged edits are represented. Staged additions count as tracked.
  Deleted tracked files are excluded. Index conflicts (non-stage-0), tracked symlinks, and
  gitlinks/submodules are refused rather than silently captured incorrectly.
- By default **untracked and ignored files are excluded**. `includeUntracked: true` opts in
  nonignored untracked files, including possibly sensitive content. Ignored files stay excluded.
  The selector rejects paths unsafe to materialize on Windows, including reserved device names,
  case collisions, bad Unicode normalization, and path traversal.

Git provenance (`HeadCommit`, selected paths, deleted/untracked counts) is returned with the
capture result but not included in the content identity. Attaching it to a durable operation
is P4, not part of this patch. Capture is not an atomic point-in-time working tree view;
concurrent edits with unchanged sizes/timestamps may escape detection. Build from a materialized
snapshot, **never** from the live Git working tree.

## Run isolated E2E tests

Native `lorelib` is RID-specific. Restore with an explicit RID:

```bash
# Linux
 dotnet restore CMakeWorkflow.Sources.Tests/CMakeWorkflow.Sources.Tests.csproj -r linux-x64
 dotnet build CMakeWorkflow.Sources.Tests/CMakeWorkflow.Sources.Tests.csproj -c Release -r linux-x64 -warnaserror
 dotnet run --project CMakeWorkflow.Sources.Tests -c Release -r linux-x64 --no-build -- self-test

# Windows: use win-x64; macOS Apple Silicon: osx-arm64
```

Manual capture and restore using the same test console:

```bash
 dotnet run --project CMakeWorkflow.Sources.Tests -r linux-x64 -- \
   capture-git /tmp/cmwf-store /path/to/repository
 dotnet run --project CMakeWorkflow.Sources.Tests -r linux-x64 -- \
   capture-git /tmp/cmwf-store /path/to/repository --include-untracked
 dotnet run --project CMakeWorkflow.Sources.Tests -r linux-x64 -- \
   restore /tmp/cmwf-store <snapshot-id> /tmp/restored-tree
```

## Deliberate limitations before durable integration

1. No Host/SDK binding or run history changes, and no execution authorized from these snapshots.
2. No cross-process/remote CAS transfer, durable manifest commits, retention pins, recovery,
   GC protocol, or guarantees against lost local Lore storage.
3. Lore `Put` uses in-memory byte arrays and has a 128 MiB **per-file** limit in this adapter.
4. Only regular files; no symlinks, gitlinks, submodules. The v1 manifest does not preserve
   executable-bit metadata, empty directories, or Git metadata (`git describe` may not work).
5. Repository must be a Git worktree root; bare repositories and nested source roots rejected.
6. No permission to elevate or modify the user's Git index. Shelling out to Git requires Git
   on `PATH`, unlike optional libgit2 integrations.

P4 should bind a captured snapshot **before** dispatch and use a verified materialized tree.
NativeAOT published Host is intentionally untouched until Lore runtime/AOT compatibility and
OS-specific tests pass.
