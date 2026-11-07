# P5c1 — Pipeline artifact federation

`FederatedArtifactCoordinator` is a read-only facade over a durable `PipelineLedger`.
All `list`, `inspect` and `fetch` calls reload the admitted pipeline stage; only
succeeded/failed, bound workflow or GitHub Actions stages expose artifacts.

For a local/SSH workflow stage supply a `WorkflowHostArtifactSource` with the existing
`WorkflowOperation` handle, `ArtifactOrigin.HostLocal` / `HostSsh`, and a stable
**trusted host + workspace identity**. The facade rejects a Host whose operation ID
differs from the stage's committed child operation ID *before* any Host RPC.
The caller is responsible for opening the Host in the correct workspace: the supplied
host identity is a configuration binding, not a cryptographically authenticated
Host attestation. The Host SDK's existing finalized catalog and resumable SHA-256
checked downloader remain authoritative; this facade creates no second Host transfer.

For GitHub Actions, inject the existing `GitHubActionsArtifactCatalog` and
`GitHubActionsArtifactDownloader`. Artifact keys retain run-only provenance: a
verified ZIP from a run is not proof of which attempt produced it. The caller
must not use these artifacts as attempt-specific dependencies without additional
trusted publisher evidence.

`FetchAsync` only creates a new destination: a regular file for a Host artifact or
an extracted directory for a GitHub ZIP. `VerifyAsync` independently checks the
local receipt's SHA-256 and exact file set. It is **offline integrity checking**, not
re-authentication of GitHub or the Host. In particular, GitHub's archive ZIP hash
cannot be recomputed after the ZIP is discarded; the receipt records the verified
ZIP hash and independent hashes of its extracted files.

Safety rules: no pipeline dispatch/cancel, no implicit network retry or reconnect,
no stage transitions, no path-based Host fetch, no global CAS, no overwriting
existing destinations. The destination directory and its parent must be caller-owned
and protected from hostile concurrent mutations.

The Host adapter and service API are the first P5c increment. Acceptance is not
complete until CI/NatAOT and end-to-end reconnect/expiring-artifact tests cover all
three sources; no claim of release readiness is made by this patch alone.

```bash
# From the existing cmwf-p5 worktree, after applying this patch:
dotnet build CMakeWorkflow.Pipelines.Federation.Tests -c Release -p:TreatWarningsAsErrors=true && \
dotnet CMakeWorkflow.Pipelines.Federation.Tests/bin/Release/net10.0/CMakeWorkflow.Pipelines.Federation.Tests.dll && \
dotnet build CMakeWorkflow.slnx -c Release -p:TreatWarningsAsErrors=true
```

## P5c2 — deterministic acceptance and CI gate

The acceptance executable reopens the SQLite ledger, reconnects using a newly constructed Host
source with the same stable host/workspace and child-operation binding, and verifies explicit
retry after a simulated transport failure. It verifies that cross-stage/cross-host keys are
not interchangeable, cancelled reads do not reach a provider, and downloaded bytes can be
rechecked offline. For GitHub Actions it starts with a non-expired run-only artifact, then
changes the provider catalog to expired and proves the old selection cannot be fetched.
It also fetches a valid GitHub ZIP through the federation facade, verifies the published
file set and rejects additional files. All catalog/fetch work leaves the pipeline
revision unchanged.

These are deterministic *fake transport* contract tests, not real network/SSH/GitHub E2E.
Real SSH reconnect against a live Host, real GitHub Actions download/expiry and NativeAOT
publication remain separate release-acceptance gates; do not infer those from a green
managed build or this test. No implicit dispatch, resubmission or retry is authorized.
