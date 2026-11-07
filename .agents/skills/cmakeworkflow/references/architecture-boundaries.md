# Capability and trust boundaries (P5 baseline)

## Actual CLI / SDK exposure

The `CMakeWorkflow` CLI routes `workflow`, `target`, `profile`, `history`, `remote`, `host`, `tooling`, `env`, and optional project-specific `dagflow` commands. Workflow manifest schema v2 is a **local workflow DAG**, not the newer cross-provider `PipelineDocument` schema v1.

The separate experimental P1–P5 libraries (`CMakeWorkflow.Pipelines`, `.Persistence`, `.Coordinator`, `.GitHub`, `.Federation`) implement the pure planner, durable SQLite ledger, Host child coordination, GitHub Actions attach/watch/dispatch, safe GitHub ZIP downloads and federated artifact coordinator. **As of the baseline commit, these do not register a `pipeline` CLI subcommand, public pipeline Host RPC, or MCP tool.** Do not claim that `CMakeWorkflow pipeline run` or `CMakeWorkflow artifacts fetch` works. Access to those libraries currently requires embedding the real .NET APIs in a trusted application/test tool.

Similarly, the VS Code extension and Host SDK are separate clients; do not infer every library feature is wired into every client surface. Read the present source before promising integration.

## Federated artifact identity (library only)

`FederatedArtifactCoordinator` provides `ListAsync`, `InspectAsync`, `FetchAsync`, `VerifyAsync` for persisted terminal pipeline stages. Local/SSH artifacts reuse the Host `WorkflowOperation` catalog and SHA-256 checked downloader. GitHub artifacts use a verified provider catalog, one-hop token-isolated download and ZIP extraction.

GitHub's Actions run artifact listing does **not** authenticate which `run_attempt` uploaded a particular artifact. The library intentionally labels them `ArtifactExecutionBinding.RunOnly`. A verified archive SHA-256 authenticates the downloaded bytes *against the provider-reported digest*, not exact attempt provenance. Extracted files are independently hashed for later offline verification; you cannot reconstruct the discarded compressed ZIP hash from the extracted directory. Do not use run-only artifacts as attempt-bound promotion inputs without stronger evidence.

`list`/`inspect`/`fetch` are read-only as to **pipeline ledger state**, but fetching writes a new caller-owned local destination. No implicit retry, no dispatcher mutation, no destination overwrite, no arbitrary remote filesystem read. Treat any future deploy/promotion as a separate mutating operation with explicit user authorization.

## Operations not implemented / not proven

- No public MCP server or agent-facing pipeline CLI in this baseline.
- No implicit remote Host bootstrap or mirroring of local manifests to the remote workspace.
- No automatic exactly-once of arbitrary external side effects; lost replies and uncertain outcomes require read-only investigation and explicit resolution.
- No automatic release/deploy, P6 distributed execution or P7 agent integrations.
- A green managed fake transport test is not by itself a live GitHub artifact-transfer or SSH artifact-reconnect E2E. Cross-platform CI and separate live SSH lifecycle jobs do not change that distinction.

## Sources

- `https://github.com/cpp20120/CMakeWorkflow/blob/a8479bd67c399fcc4f5470971e72d61a0f23104e/Commands/CliApplication.cs`
- `https://github.com/cpp20120/CMakeWorkflow/blob/a8479bd67c399fcc4f5470971e72d61a0f23104e/docs/en/pipelines.md`
- `https://github.com/cpp20120/CMakeWorkflow/blob/a8479bd67c399fcc4f5470971e72d61a0f23104e/docs/en/p5-federated-artifacts.md`
- `https://github.com/cpp20120/CMakeWorkflow/blob/a8479bd67c399fcc4f5470971e72d61a0f23104e/docs/en/p5c-federation.md`
