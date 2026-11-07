# P5 — Federated artifacts

## P5a: identity and read-only metadata (this change)

The existing Host RPC already exposes finalized operation artifact catalogs with report binding, size,
last-write evidence and SHA-256, plus chunked downloads with resumable checkpoints and final hash verification.
That Host protocol works over local as well as SSH-backed Host connections. P5 must reuse it rather than
implement a second workspace filesystem API.

The first P5 increment adds `FederatedArtifactKey`, `FederatedArtifact` and a bounded GitHub Actions catalog observer:

- The composite identity is `(origin, pipelineOperationId, stageId, sourceIdentity, artifactId)`.
  `sourceIdentity` uses provider-scoped *verified* identity, not a name/path. For GitHub, that is
  repository plus run ID; an unverified artifact attempt is never included in the source key.
- `DigestEvidence.ProviderReported` means GitHub reported a digest; only a consumer-verified download may
  produce `VerifiedLocally`. A GitHub digest is not a substitute for independently hashing fetched bytes.
- `ArtifactExecutionBinding.RunOnly` is **intentional**: GitHub's `GET /actions/runs/{run_id}/artifacts`
  endpoint does not expose a trustworthy attempt number for each artifact. Even after verifying the pinned
  run attempt and head SHA, an artifact must not be represented as produced by that particular attempt.
- The metadata adapter accepts a terminal `PipelineRunSnapshot`, derives an *attach* stage or resolves
  a *dispatch* stage from its durable P4 binding, checks the external reference and outcome-evidence
  binding, and for dispatch rechecks the `cmwf_request_id` run-name marker. It then makes only
  bounded read-only API calls. It validates the pinned attempt before listing, checks each item's run
  ID/head SHA, rejects duplicate IDs and unstable pagination, and fails closed.
  It neither dispatches nor downloads anything. Names are display-only and are never destination paths.
- `expired` and `expires_at` are informational observations; availability can change before download.

This contract is a **catalog observation**, not a verified artifact or promotion admission. In particular,
no attempt-bound use of GitHub artifacts is allowed without additional publisher evidence. P5a can
observe dispatch artifacts only after P4 has durably recorded and verified the exact run ID. The pipeline
ledger and its outcome CAS are unchanged.

## Next increments

**P5b:** Consume the existing Client/Host catalog for local and SSH stages, bind each artifact to the
recorded child operation and Host identity; add provider-scoped GitHub archive fetching with strict
byte/entry/expansion limits, path traversal and symlink refusal, no-overwrite publishing and SHA-256
verification. Do not confuse GitHub's digest definition with the compressed ZIP checksum until verified.

**P5c:** Expose `list`, `inspect`, `fetch`, `verify` through one coordinator and add persistence/recovery
cases: expired or deleted artifact, changed provider metadata, lost connection, duplicate IDs, rerun
attempts and cross-stage/host identity mismatches. No global content-addressed store or implicit retry.

## Validate

```sh
dotnet build CMakeWorkflow.slnx -c Release -p:TreatWarningsAsErrors=true
dotnet CMakeWorkflow.Pipelines.GitHub.Tests/bin/Release/net10.0/CMakeWorkflow.Pipelines.GitHub.Tests.dll
```

## P5b1: bounded GitHub ZIP verifier (incremental change)

`GitHubArtifactArchiveVerifier.ExtractAsync` consumes a stream obtained for an artifact already selected
through the P5a verified catalog. It does not fetch arbitrary URLs, dispatch GitHub workflows, or change the
pipeline ledger. Its output is a new directory, published only after checking the provider-reported SHA-256
against the **compressed ZIP bytes** and catalog archive size, validating the archive entry table and extracting all files.

The verifier rejects absent provider digests, expired artifacts, symlinks/special files, traversal, Windows
unsafe paths, collisions differing only by case, oversized archives, too many entries, excessive expanded
file lengths, corrupt ZIP data and existing destination directories. It streams archive contents to an
exclusively created temporary file under a fixed 512 MiB cap, bounds expanded data to 2 GiB and 1,024 entries,
records individual file SHA-256 values and removes staging data on failure or cancellation. A success receipt
means verified bytes against the *provider claim*, **not** a cryptographic attestation by GitHub nor proof of
which attempt uploaded the artifact. Run-only provenance stays run-only.

Security boundary: the caller owns the destination parent and must exclude concurrent hostile filesystem
mutation. This is not a generic filesystem extraction endpoint. ZIP bytes must be downloaded from the
provider-scoped artifact identity, validated by the P5a catalog. No ZIP transfer transport is in this substage;
P5b2 wires the GitHub REST `GET /repos/{owner}/{repo}/actions/artifacts/{artifact_id}/zip` redirect to the
verifier, using separate no-token archive download with redirect-host policy and explicit retries only.

Test in the P5 branch:

```sh
dotnet build CMakeWorkflow.Pipelines.GitHub.Tests -c Release -p:TreatWarningsAsErrors=true
dotnet CMakeWorkflow.Pipelines.GitHub.Tests/bin/Release/net10.0/CMakeWorkflow.Pipelines.GitHub.Tests.dll
dotnet build CMakeWorkflow.slnx -c Release -p:TreatWarningsAsErrors=true
```

## P5b2: pinned GitHub download transport

`GitHubActionsArtifactDownloader.FetchAsync` re-reads the admitted, terminal P5a catalog and
requires exact artifact metadata equality before requesting any bytes. It does not accept
arbitrary URLs from callers. The only API URL is derived from the verified repository and
numeric artifact ID: `GET /repos/{owner}/{repo}/actions/artifacts/{id}/zip`.

The GitHub API returns `302 Location` containing a short-lived signed URL. The production
`GitHubArtifactHttpTransport` disables automatic redirects and cookies; its API request
uses a bearer token only in that request message. After validating an HTTPS destination
against known GitHub Actions / Azure blob hosts, it makes a distinct token-free storage
request and refuses any additional redirect. Neither the signed URL nor its query string
is persisted or included in exceptions. If GitHub adds new storage hosts, the allowlist
must be reviewed and updated explicitly rather than accepting arbitrary URLs.

The response stream is passed to the P5b1 ZIP verifier. A successful receipt records the
verified archive SHA-256 and per-file digests, but retains `RunOnly` provenance because
GitHub's artifact listing cannot establish a specific run attempt. Network errors,
expired artifacts, changed catalog data, unexpected status, malicious redirect, damaged
archive or cancellation never commit a pipeline transition or publish a partially verified
artifact. There are no implicit retries and no resume in P5b2; a new explicit call obtains
a fresh signed URL.

Production usage owns the HTTP transport lifetime:

```csharp
using GitHubArtifactHttpTransport transport = new();
GitHubActionsArtifactDownloader downloader = new(catalog, transport, githubReadToken);
GitHubArtifactExtractionReceipt receipt = await downloader.FetchAsync(
    durablePipelineSnapshot, stageId, selectedArtifact, destinationDirectory, ct);
```

Caller requirements: the `PipelineRunSnapshot` must come from the persisted trusted ledger;
the extraction destination parent must be caller-owned and not concurrently writable by
an attacker. The P5c coordinator will own both the ledger lookup and the public APIs.
