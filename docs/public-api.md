# External .NET API

## SDK boundary

The current SDK distribution and external-consumer validation cover two assemblies:

| Assembly | External role |
| --- | --- |
| `CMakeWorkflow.Client` | Client connection, local/SSH transport options, service facades, durable operation handles, SDK request/result models and errors. |
| `CMakeWorkflow.Contracts` | Shared workflow and recipe DTOs, workflow authoring builders and node references. |

This boundary is evidenced by the [package and package-only NativeAOT consumer steps](../.github/workflows/ci.yml),
[distribution packaging](../scripts/ci-package-artifacts.ps1) and
[package dependency audit](../CMakeWorkflow.Client.AotSmoke/PackageAudit.cs). The Client project references Contracts,
StreamJsonRpc and SSH.NET; external consumers do not need references to Host, Core, CMake, History, Tooling, Profiling
or Campaigns.

This identifies the intended external .NET integration boundary. A formal source/binary compatibility policy and a
committed API compatibility baseline are not currently present in the repository. CI validates package contents and
consumer scenarios, but does not run an API compatibility diff against a previous package. Tagged release version
validation alone does not establish that compatibility.

## Implementation assemblies and other external contracts

| Assembly | Responsibility; external integration path |
| --- | --- |
| `CMakeWorkflow.Core` | Process execution and internal operation/backend contracts; consumed by in-repository adapters. |
| `CMakeWorkflow.CMake` | Planning, runner, File API, provenance, recovery and recipe implementation; use SDK workflow/recipe services or CLI. |
| `CMakeWorkflow.Host` | Server sessions, RPC dispatch, acceptance, authorization and persistence; connect through SDK or documented JSON-RPC. |
| `CMakeWorkflow.Profiling` | Capture, analysis, historical schedules and profiling adapter; use SDK profiling services or CLI. |
| `CMakeWorkflow.History` | SQLite indexing, querying and statistical implementation; use SDK History services or CLI. |
| `CMakeWorkflow.Tooling` | Tool discovery, environment probing and installers; use SDK tooling/provisioning services or CLI. |
| `CMakeWorkflow.Campaigns` | Campaign implementations; use SDK campaign services or CLI. |
| `CMakeWorkflow` | Executable and CLI dispatch; invoke its documented commands as a process. |

These eight assemblies are not part of the audited external .NET SDK package boundary. Their `public` declarations
allow cross-assembly composition inside the repository; the modifier alone does not establish an embedding or plugin
contract. Their 268 publicly accessible source types are listed separately in
[Implementation assembly API inventory](implementation-api.md).

CLI commands and the negotiated Host JSON-RPC wire protocol are external integration surfaces in their own right.
Their documented commands, method names, JSON fields, capability checks and schema versions have their own contracts.
This does not make Host CLR DTOs, `HostJson`, `DoctorSession`, ledger tables or direct SQLite access into the client SDK.
Likewise, the public `IWorkflowOperationExecutor` in Core is an internal cross-assembly extension boundary used by the
profiling adapter; it is not a third-party plugin loading API. See [architecture](en/architecture.md),
[CLI reference](en/cli.md), [Host IPC](en/host-ipc.md) and [Host read API](en/host-read-api.md).

## Internal wire DTOs

`OperationArtifactListRequest` and `OperationArtifactChunkRequest` in Client are internal wire shapes used by
`IClientRpc`, source-generated JSON metadata and operation-handle implementations. External callers use
`WorkflowOperation.ListArtifactsAsync`, `ReadArtifactChunkAsync` and `DownloadArtifactAsync` with the public artifact
models. Those public result/catalog models remain in the SDK export inventory. The corresponding Host wire shapes and
JSON fields are unchanged. See [artifact contracts](../CMakeWorkflow.Client/Workflows/OperationArtifactContracts.cs)
and [operation handle](../CMakeWorkflow.Client/Workflows/WorkflowOperation.cs).

Public errors and returned data models remain relevant to consumers even when their namespace is `Protocol` or their
implementation uses serialization. Namespace names alone do not determine support status.

## Explicit recovery revision

`WorkflowOperation.ExecuteResumeAsync(long revision, CancellationToken ct = default)` requires an explicit revision.
In the current protocol, reconciliation records revision 1 and resume admission records revision 2. The value 2 is a
persisted protocol transition, not an operation-independent retry count or evidence that an operation is admitted.
Pass the `RecoverySnapshot.Revision` returned by this operation's successful `AdmitResumeAsync`, or obtained by
`InspectRecoveryAsync` after that admission. The SDK neither fetches nor substitutes a revision for the caller.
The Host still verifies admission, quiescence, cancellation and checkpoint/attempt evidence before execution.

An RPC cancellation or lost reply does not undo committed scheduling or stop the Host-owned recovery attempt.
Inspect the original operation after an uncertain response; do not infer that it is safe to redispatch. Recovery revision
is distinct from the cancellation-intent revision. See [process recovery](en/process-execution.md#explicit-recovery-and-resume).

Code that previously constructed the two Client wire requests must use the public operation-handle methods instead.
Calls to `ExecuteResumeAsync()` must now supply the observed admission revision; the previous default argument is removed.

## Reading the signatures

Signatures retain parameter defaults, nullable annotations, generic constraints and base types. Referenced types are
namespace-qualified. Public and externally accessible protected members are included; internal/private types, test
projects, inherited members, implicit default constructors, record-synthesized equality/deconstruction members and
source-generator output are omitted. Primary constructors are shown on type declarations. Positional record properties
are represented by their constructor parameters. Attributes, method bodies and non-constant initializers remain in the
linked source.

Type descriptions come from the existing XML documentation. Member-level contracts, including cancellation,
acceptance, ownership and recovery, remain in the linked source. Type and member identities were verified against the
Release assembly metadata.

## SDK export inventory

| Assembly | Publicly accessible source types | Member entries |
| --- | ---: | ---: |
| [CMakeWorkflow.Client](#cmakeworkflowclient) | 107 | 104 |
| [CMakeWorkflow.Contracts](#cmakeworkflowcontracts) | 22 | 83 |
| **Total** | **129** | **187** |

## CMakeWorkflow.Client

### Namespace `CMakeWorkflow.Client`

#### `CMakeWorkflowClient`

Owns one initialized RPC connection and its service facades.

Service facades and operation handles borrow this client and become unusable after disposal. Request cancellation is
cooperative RPC cancellation, not a durable request to stop accepted execution. An interrupted mutation may already have
committed; the SDK does not reconnect or retry it automatically. For uncertain starts, reconnect to the same Host
workspace and find the saved ClientRequestId before deciding what to do. Connected persistent Hosts outlive this client.
Launched private Hosts are owned by the client and stop with its lifetime.

Source: [CMakeWorkflow.Client/CMakeWorkflowClient.cs](../CMakeWorkflow.Client/CMakeWorkflowClient.cs#L27), [CMakeWorkflow.Client/Transport/CMakeWorkflowClient.Ssh.cs](../CMakeWorkflow.Client/Transport/CMakeWorkflowClient.Ssh.cs#L8), [CMakeWorkflow.Client/Transport/CMakeWorkflowClient.SshNet.cs](../CMakeWorkflow.Client/Transport/CMakeWorkflowClient.SshNet.cs#L6), [CMakeWorkflow.Client/Transport/CMakeWorkflowClient.SshNetLaunch.cs](../CMakeWorkflow.Client/Transport/CMakeWorkflowClient.SshNetLaunch.cs#L6)

```csharp
public sealed class CMakeWorkflowClient : System.IAsyncDisposable
{
    public CMakeWorkflow.Client.Protocol.HostIdentity Host { get; }
    public CMakeWorkflow.Client.Workflows.WorkflowService Workflows { get; }
    public CMakeWorkflow.Client.Workflows.ExecutionService Executions { get; }
    public CMakeWorkflow.Client.Workflows.OperationService Operations { get; }
    public CMakeWorkflow.Client.Profiling.ProfileService Profiles { get; }
    public CMakeWorkflow.Client.Recipes.BenchmarkService Benchmarks { get; }
    public CMakeWorkflow.Client.Recipes.RecipeService Recipes { get; }
    public CMakeWorkflow.Client.Workflows.TargetService Targets { get; }
    public CMakeWorkflow.Client.Campaigns.CampaignService Campaigns { get; }
    public CMakeWorkflow.Client.History.HistoryService History { get; }
    public CMakeWorkflow.Client.Toolchains.ToolchainService Toolchains { get; }
    public CMakeWorkflow.Client.Provisioning.HostProvisioningService Provisioning { get; }
    public static System.Threading.Tasks.Task<CMakeWorkflow.Client.CMakeWorkflowClient> ConnectAsync(CMakeWorkflow.Client.ClientOptions options, System.Threading.CancellationToken ct = default);
    public static System.Threading.Tasks.Task<CMakeWorkflow.Client.CMakeWorkflowClient> LaunchAsync(CMakeWorkflow.Client.LaunchOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.ValueTask DisposeAsync();
    public static System.Threading.Tasks.Task<CMakeWorkflow.Client.CMakeWorkflowClient> ConnectSshAsync(CMakeWorkflow.Client.Transport.SshHostOptions options, System.Threading.CancellationToken ct = default);
    public string SshDiagnostics { get; }
    public static System.Threading.Tasks.Task<CMakeWorkflow.Client.CMakeWorkflowClient> ConnectSshNetAsync(CMakeWorkflow.Client.Transport.SshNetHostOptions options, System.Threading.CancellationToken ct = default);
    public static System.Threading.Tasks.Task<CMakeWorkflow.Client.CMakeWorkflowClient> LaunchSshNetAsync(CMakeWorkflow.Client.Transport.SshNetLaunchOptions options, System.Threading.CancellationToken ct = default);
}
```

#### `ClientOptions`

Options for local Host connection establishment.

Source: [CMakeWorkflow.Client/ClientOptions.cs](../CMakeWorkflow.Client/ClientOptions.cs#L6)

```csharp
public sealed record ClientOptions(string Endpoint, string Workspace);
```

#### `LaunchOptions`

Options for local Host connection establishment.

Source: [CMakeWorkflow.Client/LaunchOptions.cs](../CMakeWorkflow.Client/LaunchOptions.cs#L7)

```csharp
public sealed record LaunchOptions(string Executable, string Workspace, string? Endpoint = null);
```

### Namespace `CMakeWorkflow.Client.Campaigns`

#### `CampaignResult`

Source: [CMakeWorkflow.Client/Campaigns/CampaignResult.cs](../CMakeWorkflow.Client/Campaigns/CampaignResult.cs#L3)

```csharp
public sealed record CampaignResult(int SchemaVersion, string Kind, string Status, int ExitCode, string? ArtifactDirectory);
```

#### `CampaignService`

Runs request/connection-scoped Host campaigns; borrows the client lifetime.

Campaigns can execute tools and write corpus/report artifacts. Cancellation/disconnect can leave partial effects. These
methods do not return durable operation handles, promise idempotency or automatically retry/reconnect. All paths are
interpreted on the initialized Host.

Source: [CMakeWorkflow.Client/Campaigns/CampaignService.cs](../CMakeWorkflow.Client/Campaigns/CampaignService.cs#L9)

```csharp
public sealed class CampaignService(CMakeWorkflow.Client.CMakeWorkflowClient client)
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Campaigns.CampaignResult> MutationAsync(CMakeWorkflow.Client.Campaigns.MutationCampaignOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Campaigns.CampaignResult> WeakMemoryAsync(CMakeWorkflow.Client.Campaigns.WeakMemoryCampaignOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Campaigns.CampaignResult> FuzzSeedAsync(CMakeWorkflow.Client.Campaigns.FuzzSeedCampaignOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Campaigns.CampaignResult> FuzzRunAsync(CMakeWorkflow.Client.Campaigns.FuzzRunCampaignOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Campaigns.CampaignResult> FuzzTriageAsync(CMakeWorkflow.Client.Campaigns.FuzzTriageCampaignOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Campaigns.CampaignResult> FuzzMergeAsync(CMakeWorkflow.Client.Campaigns.FuzzMergeCampaignOptions options, System.Threading.CancellationToken ct = default);
}
```

#### `FuzzMergeCampaignOptions`

Source: [CMakeWorkflow.Client/Campaigns/FuzzMergeCampaignOptions.cs](../CMakeWorkflow.Client/Campaigns/FuzzMergeCampaignOptions.cs#L3)

```csharp
public sealed record FuzzMergeCampaignOptions(string RunDirectory, string? OutputDirectory = null, bool Coverage = false);
```

#### `FuzzRunCampaignOptions`

Source: [CMakeWorkflow.Client/Campaigns/FuzzRunCampaignOptions.cs](../CMakeWorkflow.Client/Campaigns/FuzzRunCampaignOptions.cs#L3)

```csharp
public sealed record FuzzRunCampaignOptions(string BuildDirectory, string Layer, string ResultsDirectory, string? Backend = null, string? CorpusDirectory = null, string? Configuration = null, int DurationSeconds = 60, int InputTimeoutSeconds = 15, int MaxLength = 4096, bool ReplayOnly = false);
```

#### `FuzzSeedCampaignOptions`

Source: [CMakeWorkflow.Client/Campaigns/FuzzSeedCampaignOptions.cs](../CMakeWorkflow.Client/Campaigns/FuzzSeedCampaignOptions.cs#L3)

```csharp
public sealed record FuzzSeedCampaignOptions(string Layer, string OutputDirectory);
```

#### `FuzzTriageCampaignOptions`

Source: [CMakeWorkflow.Client/Campaigns/FuzzTriageCampaignOptions.cs](../CMakeWorkflow.Client/Campaigns/FuzzTriageCampaignOptions.cs#L3)

```csharp
public sealed record FuzzTriageCampaignOptions(string RunDirectory);
```

#### `MutationCampaignOptions`

Source: [CMakeWorkflow.Client/Campaigns/MutationCampaignOptions.cs](../CMakeWorkflow.Client/Campaigns/MutationCampaignOptions.cs#L3)

```csharp
public sealed record MutationCampaignOptions(string SourceDirectory, string? ReportPath = null, string Suite = "all", string Compiler = "c++", string Seed = "496833221358", int Cases = 48, int Steps = 48, int TimeoutSeconds = 30);
```

#### `WeakMemoryCampaignOptions`

Source: [CMakeWorkflow.Client/Campaigns/WeakMemoryCampaignOptions.cs](../CMakeWorkflow.Client/Campaigns/WeakMemoryCampaignOptions.cs#L3)

```csharp
public sealed record WeakMemoryCampaignOptions(string SourceDirectory, string RelacyDirectory, string? ReportPath = null, string Protocol = "all", string Compiler = "c++", int TimeoutSeconds = 60);
```

### Namespace `CMakeWorkflow.Client.History`

#### `HistoryArtifact`

Source: [CMakeWorkflow.Client/History/HistoryReadModels.cs](../CMakeWorkflow.Client/History/HistoryReadModels.cs#L6)

```csharp
public sealed record HistoryArtifact(string RunId, string? NodeId, string Type, string Path, string? Sha256, string? Metadata, bool Available = true);
```

#### `HistoryBuildEnvironment`

Source: [CMakeWorkflow.Client/History/HistoryReadModels.cs](../CMakeWorkflow.Client/History/HistoryReadModels.cs#L15)

```csharp
public sealed record HistoryBuildEnvironment(string Directory, string? Configuration, string? Generator, string? Compiler, string? CompilerId, string? CompilerVersion, string? Flags, string Fingerprint);
```

#### `HistoryCompareQuery`

Source: [CMakeWorkflow.Client/History/HistoryReadQueries.cs](../CMakeWorkflow.Client/History/HistoryReadQueries.cs#L19)

```csharp
public sealed record HistoryCompareQuery(string DatabasePath, string BaselineRunId, string CandidateRunId);
```

#### `HistoryDetail`

Source: [CMakeWorkflow.Client/History/HistoryReadModels.cs](../CMakeWorkflow.Client/History/HistoryReadModels.cs#L25)

```csharp
public sealed record HistoryDetail(CMakeWorkflow.Client.History.HistoryRun Run, System.Collections.Generic.List<CMakeWorkflow.Client.History.HistoryNode> Nodes, System.Collections.Generic.List<CMakeWorkflow.Client.History.HistoryMeasurement> Measurements, System.Collections.Generic.List<CMakeWorkflow.Client.History.HistoryArtifact> Artifacts);
```

#### `HistoryDetectQuery`

Source: [CMakeWorkflow.Client/History/HistoryReadQueries.cs](../CMakeWorkflow.Client/History/HistoryReadQueries.cs#L21)

```csharp
public sealed record HistoryDetectQuery(string DatabasePath, string BaselineRunId, string CandidateRunId, string Metric, string Direction, double MinimumEffectPercent, string? Variant = null, string? Scenario = null, double Alpha = 0.05);
```

#### `HistoryDetection`

Source: [CMakeWorkflow.Client/History/HistoryReadModels.cs](../CMakeWorkflow.Client/History/HistoryReadModels.cs#L114)

```csharp
public sealed record HistoryDetection(string Metric, string Unit, string? Variant, string? Scenario, string Method, string Status, string Reason, CMakeWorkflow.Client.History.HistoryStats? Baseline, CMakeWorkflow.Client.History.HistoryStats? Candidate, double? AbsoluteChange, double? PercentChange, double? PValue, double? AdjustedPValue, int? Permutations, bool? Exact);
```

#### `HistoryDetectionReport`

Source: [CMakeWorkflow.Client/History/HistoryReadModels.cs](../CMakeWorkflow.Client/History/HistoryReadModels.cs#L131)

```csharp
public sealed record HistoryDetectionReport(int SchemaVersion, string BaselineRun, string CandidateRun, string Metric, string Direction, double MinimumEffectPercent, double Alpha, string Test, System.Collections.Generic.List<CMakeWorkflow.Client.History.HistoryDetection> Results);
```

#### `HistoryDifference`

Source: [CMakeWorkflow.Client/History/HistoryReadModels.cs](../CMakeWorkflow.Client/History/HistoryReadModels.cs#L101)

```csharp
public sealed record HistoryDifference(string Metric, string Unit, string? Variant, string? Scenario, string Method, string Status, string? Reason, CMakeWorkflow.Client.History.HistoryStats? Baseline, CMakeWorkflow.Client.History.HistoryStats? Candidate, double? AbsoluteChange, double? PercentChange);
```

#### `HistoryHostEnvironment`

Source: [CMakeWorkflow.Client/History/HistoryReadModels.cs](../CMakeWorkflow.Client/History/HistoryReadModels.cs#L35)

```csharp
public sealed record HistoryHostEnvironment(string? HostId, string? CpuModel, string? Architecture, int? LogicalProcessors, int? AvailableProcessors, int? PhysicalPackages, int? PhysicalCores, string? OperatingSystem, string? KernelVersion, string? Affinity, string? Governor, string? Fingerprint);
```

#### `HistoryMeasurement`

Source: [CMakeWorkflow.Client/History/HistoryReadModels.cs](../CMakeWorkflow.Client/History/HistoryReadModels.cs#L49)

```csharp
public sealed record HistoryMeasurement(string RunId, string? NodeId, string Metric, string Unit, double Value, string? Variant, string? Scenario, string Source, string Method, int SampleIndex, string? Workload, string? Environment);
```

#### `HistoryMetricsQuery`

Source: [CMakeWorkflow.Client/History/HistoryReadQueries.cs](../CMakeWorkflow.Client/History/HistoryReadQueries.cs#L16)

```csharp
public sealed record HistoryMetricsQuery(string DatabasePath, string? RunId = null, string? Metric = null, string? Variant = null, string? Scenario = null);
```

#### `HistoryNode`

Source: [CMakeWorkflow.Client/History/HistoryReadModels.cs](../CMakeWorkflow.Client/History/HistoryReadModels.cs#L63)

```csharp
public sealed record HistoryNode(string RunId, string Id, string Kind, string Status, double? DurationSeconds, string? Dependencies, string? BuildDirectory, string? Configuration, string? Target);
```

#### `HistoryRun`

Source: [CMakeWorkflow.Client/History/HistoryReadModels.cs](../CMakeWorkflow.Client/History/HistoryReadModels.cs#L74)

```csharp
public sealed record HistoryRun(string Id, string Directory, string Format, string? Workflow, string? ManifestHash, string? StartedAt, string? FinishedAt, string Status, string? Revision, string SourceHash, bool SourceAvailable = true, string? HostId = null, string? EnvironmentFingerprint = null, CMakeWorkflow.Client.History.HistoryHostEnvironment? Environment = null, System.Collections.Generic.List<CMakeWorkflow.Client.History.HistoryBuildEnvironment>? BuildEnvironments = null);
```

#### `HistoryRunPage`

Source: [CMakeWorkflow.Client/History/HistoryRunPage.cs](../CMakeWorkflow.Client/History/HistoryRunPage.cs#L4)

```csharp
public sealed record HistoryRunPage(int SchemaVersion, System.Collections.Generic.List<CMakeWorkflow.Client.History.HistoryRun> Items, string? NextCursor);
```

#### `HistoryRunsPageQuery`

Browse historical runs newest-first with an opaque workspace-bound cursor.

Importing new runs does not insert them into an existing traversal. Reimports and deletions are not snapshot-isolated.

Source: [CMakeWorkflow.Client/History/HistoryReadQueries.cs](../CMakeWorkflow.Client/History/HistoryReadQueries.cs#L10)

```csharp
public sealed record HistoryRunsPageQuery(string DatabasePath, int Limit = 50, string? Status = null, string? Since = null, string? Until = null, string? Workflow = null, string? Variant = null, string? Cursor = null);
```

#### `HistoryRunsQuery`

Source: [CMakeWorkflow.Client/History/HistoryReadQueries.cs](../CMakeWorkflow.Client/History/HistoryReadQueries.cs#L4)

```csharp
public sealed record HistoryRunsQuery(string DatabasePath, int Limit = 50, string? Status = null, string? Since = null, string? Until = null, string? Workflow = null, string? Variant = null);
```

#### `HistoryService`

Typed reads, analysis and imports over a Host-owned history database.

Database and run paths are interpreted on the initialized Host. This facade borrows the client lifetime. Cancellation is
request-scoped and RPC failures propagate without automatic reconnect or retry.

Source: [CMakeWorkflow.Client/History/HistoryService.cs](../CMakeWorkflow.Client/History/HistoryService.cs#L8)

```csharp
public sealed class HistoryService(CMakeWorkflow.Client.CMakeWorkflowClient client)
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.History.ImportHistoryResult> ImportAsync(CMakeWorkflow.Client.History.ImportHistoryOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<System.Collections.Generic.List<CMakeWorkflow.Client.History.HistoryRun>> RunsAsync(CMakeWorkflow.Client.History.HistoryRunsQuery query, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.History.HistoryRunPage> RunsPageAsync(CMakeWorkflow.Client.History.HistoryRunsPageQuery query, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.History.HistoryDetail> ShowAsync(CMakeWorkflow.Client.History.HistoryShowQuery query, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<System.Collections.Generic.List<CMakeWorkflow.Client.History.HistoryMeasurement>> MetricsAsync(CMakeWorkflow.Client.History.HistoryMetricsQuery query, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<System.Collections.Generic.List<CMakeWorkflow.Client.History.HistoryMeasurement>> QueryAsync(CMakeWorkflow.Client.History.HistoryMetricsQuery query, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<System.Collections.Generic.List<CMakeWorkflow.Client.History.HistoryDifference>> CompareAsync(CMakeWorkflow.Client.History.HistoryCompareQuery query, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.History.HistoryDetectionReport> DetectAsync(CMakeWorkflow.Client.History.HistoryDetectQuery query, System.Threading.CancellationToken ct = default);
}
```

#### `HistoryShowQuery`

Source: [CMakeWorkflow.Client/History/HistoryReadQueries.cs](../CMakeWorkflow.Client/History/HistoryReadQueries.cs#L14)

```csharp
public sealed record HistoryShowQuery(string DatabasePath, string RunId);
```

#### `HistoryStats`

Source: [CMakeWorkflow.Client/History/HistoryReadModels.cs](../CMakeWorkflow.Client/History/HistoryReadModels.cs#L93)

```csharp
public sealed record HistoryStats(int Count, double Median, double Min, double Max, double Mean, double StandardDeviation);
```

#### `ImportHistoryOptions`

Source: [CMakeWorkflow.Client/History/ImportHistoryOptions.cs](../CMakeWorkflow.Client/History/ImportHistoryOptions.cs#L3)

```csharp
public sealed record ImportHistoryOptions(string DatabasePath, string Directory, bool Recursive = false);
```

#### `ImportHistoryResult`

Source: [CMakeWorkflow.Client/History/ImportHistoryResult.cs](../CMakeWorkflow.Client/History/ImportHistoryResult.cs#L3)

```csharp
public sealed record ImportHistoryResult(int SchemaVersion, int Imported, int Unchanged, int Skipped);
```

### Namespace `CMakeWorkflow.Client.Profiling`

#### `AnalyzeProfileOptions`

Source: [CMakeWorkflow.Client/Profiling/AnalyzeProfileOptions.cs](../CMakeWorkflow.Client/Profiling/AnalyzeProfileOptions.cs#L3)

```csharp
public sealed record AnalyzeProfileOptions(string RunDirectory);
```

#### `AnalyzedProfile`

Source: [CMakeWorkflow.Client/Profiling/AnalyzedProfile.cs](../CMakeWorkflow.Client/Profiling/AnalyzedProfile.cs#L3)

```csharp
public sealed record AnalyzedProfile(int SchemaVersion, string RunDirectory, string AnalysisFile);
```

#### `PrepareProfileOptions`

Source: [CMakeWorkflow.Client/Profiling/PrepareProfileOptions.cs](../CMakeWorkflow.Client/Profiling/PrepareProfileOptions.cs#L4)

```csharp
public sealed record PrepareProfileOptions(string RequestId, string ResultsDirectory, string Target, System.Collections.Generic.List<CMakeWorkflow.Client.Profiling.ProfileVariantOptions> Variants, int Repeats = 3, double TimeoutSeconds = 300, string Backend = "auto", string Mode = "cpu", string? Configuration = null, string? SourceDirectory = null, string? ConfigurePreset = null, string[]? Arguments = null);
```

#### `PrepareReplayProfileOptions`

Identifies two saved runs and the node to replay. `ResultsDirectory` is the Host directory for the prepared manifest.

Source: [CMakeWorkflow.Client/Profiling/PrepareReplayProfileOptions.cs](../CMakeWorkflow.Client/Profiling/PrepareReplayProfileOptions.cs#L4)

```csharp
public sealed record PrepareReplayProfileOptions(string RequestId, string ResultsDirectory, string BaselineDirectory, string CandidateDirectory, string NodeId, string? CaseName = null, int HarnessIndex = 0, string Backend = "auto", double TimeoutSeconds = 300);
```

#### `ProfileService`

Host profile preparation, analysis and request-scoped replay; borrows the client lifetime.

Paths are interpreted on the Host. Calls propagate cancellation and transport failures without automatic retry. Replay
executes work under the request and connection lifetime. `PrepareReplayAsync` prepares a replay manifest for explicit
durable admission through `Workflows.StartAsync`.

Source: [CMakeWorkflow.Client/Profiling/ProfileService.cs](../CMakeWorkflow.Client/Profiling/ProfileService.cs#L11)

```csharp
public sealed class ProfileService(CMakeWorkflow.Client.CMakeWorkflowClient client)
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.PreparedWorkflow> PrepareAsync(CMakeWorkflow.Client.Profiling.PrepareProfileOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Profiling.AnalyzedProfile> AnalyzeAsync(CMakeWorkflow.Client.Profiling.AnalyzeProfileOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.PreparedWorkflow> PrepareReplayAsync(CMakeWorkflow.Client.Profiling.PrepareReplayProfileOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Profiling.ReplayProfileResult> ReplayAsync(CMakeWorkflow.Client.Profiling.ReplayProfileOptions options, System.Threading.CancellationToken ct = default);
}
```

#### `ProfileVariantOptions`

Source: [CMakeWorkflow.Client/Profiling/ProfileVariantOptions.cs](../CMakeWorkflow.Client/Profiling/ProfileVariantOptions.cs#L5)

```csharp
public sealed record ProfileVariantOptions(string Name, string BuildDirectory, CMakeWorkflow.Contracts.Workflows.WorkflowToolchainBinding? Toolchain = null);
```

#### `ReplayProfileOptions`

Source: [CMakeWorkflow.Client/Profiling/ReplayProfileOptions.cs](../CMakeWorkflow.Client/Profiling/ReplayProfileOptions.cs#L3)

```csharp
public sealed record ReplayProfileOptions(string BaselineDirectory, string CandidateDirectory, string NodeId, string ResultsDirectory, string? CaseName = null, int HarnessIndex = 0, string Backend = "auto", double TimeoutSeconds = 300);
```

#### `ReplayProfileResult`

Source: [CMakeWorkflow.Client/Profiling/ReplayProfileResult.cs](../CMakeWorkflow.Client/Profiling/ReplayProfileResult.cs#L3)

```csharp
public sealed record ReplayProfileResult(int SchemaVersion, string RunDirectory, string AnalysisFile);
```

### Namespace `CMakeWorkflow.Client.Protocol`

#### `CMakeWorkflowRpcRefusalException`

A stable Host application refusal with a machine-readable code.

Source: [CMakeWorkflow.Client/Protocol/RpcRefusals.cs](../CMakeWorkflow.Client/Protocol/RpcRefusals.cs#L8)

```csharp
public sealed class CMakeWorkflowRpcRefusalException(string code, string message, System.Exception inner) : System.Exception
{
    public string Code { get; }
}
```

#### `HostIdentity`

Source: [CMakeWorkflow.Client/Protocol/HostIdentity.cs](../CMakeWorkflow.Client/Protocol/HostIdentity.cs#L5)

```csharp
public sealed record HostIdentity(int ProtocolVersion, int ProcessId, string WorkspaceDirectory, string[] Capabilities, System.Text.Json.JsonElement Profiles);
```

#### `RpcErrorData`

Source: [CMakeWorkflow.Client/Protocol/RpcErrorData.cs](../CMakeWorkflow.Client/Protocol/RpcErrorData.cs#L3)

```csharp
public sealed record RpcErrorData(string Code, string Message);
```

### Namespace `CMakeWorkflow.Client.Provisioning`

#### `HostApplyFixOptions`

Source: [CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs](../CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs#L26)

```csharp
public sealed record HostApplyFixOptions(string OperationId, string PlanId);
```

#### `HostDoctorOptions`

Source: [CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs](../CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs#L14)

```csharp
public sealed record HostDoctorOptions(string OperationId, string[] Profiles, string? InstallDirectory = null);
```

#### `HostFixPlan`

Source: [CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs](../CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs#L60)

```csharp
public sealed record HostFixPlan(int SchemaVersion, string PlanId, System.DateTimeOffset ExpiresAt, CMakeWorkflow.Client.Provisioning.HostToolingPlan Plan, string? RunDirectory = null, string? RunSnapshot = null);
```

#### `HostFixResult`

Source: [CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs](../CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs#L62)

```csharp
public sealed record HostFixResult(int SchemaVersion, string Status, CMakeWorkflow.Client.Provisioning.HostToolingResult? Installation, CMakeWorkflow.Client.Provisioning.HostToolingPlan? Verification, string[] Diagnostics, bool ReadyToResume = false);
```

#### `HostPackageManagerKind`

Source: [CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs](../CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs#L11)

```csharp
public enum HostPackageManagerKind
{
    Auto = 0,
    None = 1,
    Apt = 2,
    Pacman = 3,
    Dnf = 4,
    Brew = 5,
    Winget = 6,
}
```

#### `HostPlanFixOptions`

Source: [CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs](../CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs#L19)

```csharp
public sealed record HostPlanFixOptions(string[] Profiles, string? InstallDirectory = null, CMakeWorkflow.Client.Provisioning.HostPackageManagerKind PackageManager = Auto, bool AllowFallback = true, string? RunDirectory = null);
```

#### `HostProvisioningService`

Host-side tool inspection and explicit, single-use fix-plan execution.

Source: [CMakeWorkflow.Client/Provisioning/HostProvisioningService.cs](../CMakeWorkflow.Client/Provisioning/HostProvisioningService.cs#L6)

```csharp
public sealed class HostProvisioningService(CMakeWorkflow.Client.CMakeWorkflowClient client)
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Provisioning.HostToolingPlan> DoctorAsync(CMakeWorkflow.Client.Provisioning.HostDoctorOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Provisioning.HostFixPlan> PlanFixAsync(CMakeWorkflow.Client.Provisioning.HostPlanFixOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Provisioning.HostFixResult> ApplyFixAsync(CMakeWorkflow.Client.Provisioning.HostApplyFixOptions options, System.Threading.CancellationToken ct = default);
}
```

#### `HostToolPlatform`

Source: [CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs](../CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs#L8)

```csharp
public enum HostToolPlatform
{
    Linux = 0,
    MacOS = 1,
    Windows = 2,
}
```

#### `HostToolStatus`

Source: [CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs](../CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs#L28)

```csharp
public sealed record HostToolStatus(string Id, string DisplayName, bool Installed, string? Command, string? Version, string? MinimumVersion, string? Diagnostic);
```

#### `HostToolingPlan`

Source: [CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs](../CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs#L36)

```csharp
public sealed record HostToolingPlan(int SchemaVersion, CMakeWorkflow.Client.Provisioning.HostToolPlatform Platform, string[] Profiles, string InstallDirectory, CMakeWorkflow.Client.Provisioning.HostPackageManagerKind PackageManager, bool Ready, System.Collections.Generic.List<CMakeWorkflow.Client.Provisioning.HostToolStatus> Tools, System.Collections.Generic.List<CMakeWorkflow.Client.Provisioning.HostToolingStep> Steps);
```

#### `HostToolingResult`

Source: [CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs](../CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs#L46)

```csharp
public sealed record HostToolingResult(int SchemaVersion, bool Success, string[] Ready, string[] Failed, string[] Diagnostics);
```

#### `HostToolingStep`

Source: [CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs](../CMakeWorkflow.Client/Provisioning/HostProvisioningModels.cs#L30)

```csharp
public sealed record HostToolingStep(string Tool, string Method, string? Executable, string[] Arguments, string? Source, string? Diagnostic);
```

#### `SshNetHostProvisioner`

Opt-in deployment of an already published Linux Host. Builds a deterministic plan, checks an explicit approval digest,
uploads via pinned SFTP, verifies bytes on the remote machine and atomically activates a versioned directory. Never
invokes sudo or installs system packages. No persistent Host is killed. The exclusive directory lock fails closed after
a crashed installer: remove it manually only after confirming that no installer is still running.

Source: [CMakeWorkflow.Client/Provisioning/SshNetHostProvisioner.cs](../CMakeWorkflow.Client/Provisioning/SshNetHostProvisioner.cs#L16)

```csharp
public static class SshNetHostProvisioner
{
    public static CMakeWorkflow.Client.Provisioning.SshNetProvisionPlan Plan(CMakeWorkflow.Client.Provisioning.SshNetProvisionOptions options, System.Threading.CancellationToken ct = default);
    public static CMakeWorkflow.Client.Provisioning.SshNetProvisionReceipt Apply(CMakeWorkflow.Client.Provisioning.SshNetProvisionOptions options, string approvedSha256, System.Threading.CancellationToken ct = default);
}
```

#### `SshNetProvisionFile`

Source: [CMakeWorkflow.Client/Provisioning/SshNetProvisionOptions.cs](../CMakeWorkflow.Client/Provisioning/SshNetProvisionOptions.cs#L12)

```csharp
public sealed record SshNetProvisionFile(string RelativePath, string Sha256, long Size);
```

#### `SshNetProvisionOptions`

Explicit, user-scoped Linux Host installation over authenticated SSH.NET.

Source: [CMakeWorkflow.Client/Provisioning/SshNetProvisionOptions.cs](../CMakeWorkflow.Client/Provisioning/SshNetProvisionOptions.cs#L4)

```csharp
public sealed record SshNetProvisionOptions(string Host, string Username, string PrivateKeyPath, string HostKeySha256, string Workspace, string PublishDirectory, string InstallRoot, string RuntimeIdentifier)
{
    public int Port { get; init; }
    public System.TimeSpan ConnectionTimeout { get; init; }
}
```

#### `SshNetProvisionPlan`

Read-only plan. Approval binds host identity, target, RID and every local file hash.

Source: [CMakeWorkflow.Client/Provisioning/SshNetProvisionOptions.cs](../CMakeWorkflow.Client/Provisioning/SshNetProvisionOptions.cs#L15)

```csharp
public sealed record SshNetProvisionPlan(string ApprovalSha256, string RuntimeIdentifier, string RemoteArchitecture, string InstallRoot, string ReleaseDirectory, string Executable, bool AlreadyActive, string[] ToolProbes, CMakeWorkflow.Client.Provisioning.SshNetProvisionFile[] Files);
```

#### `SshNetProvisionReceipt`

Source: [CMakeWorkflow.Client/Provisioning/SshNetProvisionOptions.cs](../CMakeWorkflow.Client/Provisioning/SshNetProvisionOptions.cs#L28)

```csharp
public sealed record SshNetProvisionReceipt(string ApprovalSha256, string ReleaseDirectory, string Executable, bool AlreadyActive);
```

### Namespace `CMakeWorkflow.Client.Recipes`

#### `BenchmarkService`

Prepares benchmark recipes on the connected Host; borrows the client lifetime.

Source: [CMakeWorkflow.Client/Recipes/BenchmarkService.cs](../CMakeWorkflow.Client/Recipes/BenchmarkService.cs#L4)

```csharp
public sealed class BenchmarkService(CMakeWorkflow.Client.CMakeWorkflowClient client)
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Recipes.PreparedBenchmark> PrepareAsync(CMakeWorkflow.Client.Recipes.PrepareBenchmarkOptions options, System.Threading.CancellationToken ct = default);
}
```

#### `PrepareBenchmarkMatrixOptions`

Inputs for commit-once Host recipe preparation.

Source: [CMakeWorkflow.Client/Recipes/PrepareBenchmarkMatrixOptions.cs](../CMakeWorkflow.Client/Recipes/PrepareBenchmarkMatrixOptions.cs#L9)

```csharp
public sealed record PrepareBenchmarkMatrixOptions(string RequestId, string OutputPath, CMakeWorkflow.Contracts.Recipes.BenchmarkMatrixRecipeV1 Recipe);
```

#### `PrepareBenchmarkOptions`

Inputs for commit-once Host recipe preparation.

Source: [CMakeWorkflow.Client/Recipes/PrepareBenchmarkOptions.cs](../CMakeWorkflow.Client/Recipes/PrepareBenchmarkOptions.cs#L9)

```csharp
public sealed record PrepareBenchmarkOptions(string RequestId, string OutputPath, CMakeWorkflow.Contracts.Recipes.BenchmarkRecipeV1 Recipe);
```

#### `PrepareBuildMatrixOptions`

Inputs for commit-once Host recipe preparation.

Source: [CMakeWorkflow.Client/Recipes/PrepareBuildMatrixOptions.cs](../CMakeWorkflow.Client/Recipes/PrepareBuildMatrixOptions.cs#L9)

```csharp
public sealed record PrepareBuildMatrixOptions(string RequestId, string OutputPath, CMakeWorkflow.Contracts.Recipes.BuildMatrixRecipeV1 Recipe);
```

#### `PrepareProfileRecipeOptions`

Inputs for commit-once Host recipe preparation.

Source: [CMakeWorkflow.Client/Recipes/PrepareProfileRecipeOptions.cs](../CMakeWorkflow.Client/Recipes/PrepareProfileRecipeOptions.cs#L9)

```csharp
public sealed record PrepareProfileRecipeOptions(string RequestId, string OutputPath, CMakeWorkflow.Contracts.Recipes.ProfileRecipeV1 Recipe);
```

#### `PrepareTestMatrixOptions`

Inputs for commit-once Host recipe preparation.

Source: [CMakeWorkflow.Client/Recipes/PrepareTestMatrixOptions.cs](../CMakeWorkflow.Client/Recipes/PrepareTestMatrixOptions.cs#L9)

```csharp
public sealed record PrepareTestMatrixOptions(string RequestId, string OutputPath, CMakeWorkflow.Contracts.Recipes.TestMatrixRecipeV1 Recipe);
```

#### `PreparedBenchmark`

Prepared Host manifest evidence; does not imply admission or execution.

Source: [CMakeWorkflow.Client/Recipes/PreparedBenchmark.cs](../CMakeWorkflow.Client/Recipes/PreparedBenchmark.cs#L7)

```csharp
public sealed record PreparedBenchmark(int SchemaVersion, string Manifest, string ManifestSha256);
```

#### `PreparedRecipe`

Prepared Host manifest evidence; does not imply admission or execution.

Source: [CMakeWorkflow.Client/Recipes/PreparedRecipe.cs](../CMakeWorkflow.Client/Recipes/PreparedRecipe.cs#L7)

```csharp
public sealed record PreparedRecipe(int SchemaVersion, string Manifest, string ManifestSha256);
```

#### `RecipeService`

Prepares typed recipes into immutable Host manifest files.

All paths belong to the initialized Host workspace. Preparation persists a request binding before the manifest. An
interrupted write may leave that binding or the manifest committed; retry explicitly with the same request and output.
Conflicting committed bytes are refused. Preparation neither executes nodes nor admits a durable workflow operation.
This facade borrows the client lifetime and does not automatically reconnect or retry.

Source: [CMakeWorkflow.Client/Recipes/RecipeService.cs](../CMakeWorkflow.Client/Recipes/RecipeService.cs#L10)

```csharp
public sealed class RecipeService(CMakeWorkflow.Client.CMakeWorkflowClient client)
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Recipes.PreparedRecipe> PrepareProfileAsync(CMakeWorkflow.Client.Recipes.PrepareProfileRecipeOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Recipes.PreparedRecipe> PrepareTestMatrixAsync(CMakeWorkflow.Client.Recipes.PrepareTestMatrixOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Recipes.PreparedRecipe> PrepareBuildMatrixAsync(CMakeWorkflow.Client.Recipes.PrepareBuildMatrixOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Recipes.PreparedRecipe> PrepareBenchmarkMatrixAsync(CMakeWorkflow.Client.Recipes.PrepareBenchmarkMatrixOptions options, System.Threading.CancellationToken ct = default);
}
```

### Namespace `CMakeWorkflow.Client.Toolchains`

#### `DeveloperEnvironmentRequest`

Source: [CMakeWorkflow.Client/Toolchains/DeveloperEnvironmentRequest.cs](../CMakeWorkflow.Client/Toolchains/DeveloperEnvironmentRequest.cs#L3)

```csharp
public sealed record DeveloperEnvironmentRequest(string EnvironmentProfileId, string HostArchitecture = "x64", string TargetArchitecture = "x64", string? WindowsSdkVersion = null, string? ToolsetVersion = null);
```

#### `DeveloperEnvironmentSnapshot`

Source: [CMakeWorkflow.Client/Toolchains/DeveloperEnvironmentSnapshot.cs](../CMakeWorkflow.Client/Toolchains/DeveloperEnvironmentSnapshot.cs#L4)

```csharp
public sealed record DeveloperEnvironmentSnapshot(int SchemaVersion, string EnvironmentProfileId, string HostArchitecture, string TargetArchitecture, string Fingerprint, System.Collections.Generic.Dictionary<string, string> VisibleVariables, System.Collections.Generic.List<string> Diagnostics);
```

#### `ToolchainCandidate`

Source: [CMakeWorkflow.Client/Toolchains/ToolchainCandidate.cs](../CMakeWorkflow.Client/Toolchains/ToolchainCandidate.cs#L4)

```csharp
public sealed record ToolchainCandidate(string Id, string Role, string Flavor, string Provider, string Path, string? Version = null, string? VisualStudioInstanceId = null, string? EnvironmentProfileId = null);
```

#### `ToolchainInventory`

Source: [CMakeWorkflow.Client/Toolchains/ToolchainInventory.cs](../CMakeWorkflow.Client/Toolchains/ToolchainInventory.cs#L4)

```csharp
public sealed record ToolchainInventory(int SchemaVersion, string Platform, System.Collections.Generic.List<CMakeWorkflow.Client.Toolchains.VisualStudioInstance> VisualStudio, System.Collections.Generic.List<CMakeWorkflow.Client.Toolchains.ToolchainCandidate> Candidates, System.Collections.Generic.List<string> Diagnostics);
```

#### `ToolchainResolution`

Source: [CMakeWorkflow.Client/Toolchains/ToolchainResolution.cs](../CMakeWorkflow.Client/Toolchains/ToolchainResolution.cs#L4)

```csharp
public sealed record ToolchainResolution(int SchemaVersion, string Status, CMakeWorkflow.Client.Toolchains.ToolchainCandidate? CMake, CMakeWorkflow.Client.Toolchains.ToolchainCandidate? Ninja, CMakeWorkflow.Client.Toolchains.ToolchainCandidate? Compiler, CMakeWorkflow.Client.Toolchains.ToolchainCandidate? Sdk, string? EnvironmentProfileId, System.Collections.Generic.List<string> Diagnostics);
```

#### `ToolchainSelection`

Source: [CMakeWorkflow.Client/Toolchains/ToolchainSelection.cs](../CMakeWorkflow.Client/Toolchains/ToolchainSelection.cs#L5)

```csharp
public sealed record ToolchainSelection(string? CMakeCandidateId = null, string? NinjaCandidateId = null, string? CompilerCandidateId = null, string? SdkCandidateId = null, string? EnvironmentProfileId = null, string Generator = "Ninja");
```

#### `ToolchainService`

Host-local toolchain inspection facade borrowing the client lifetime.

Queries do not establish future tool availability or reserve candidates. Workflow admission resolves IDs afresh.
Cancellation affects the query only; the SDK does not reconnect or retry automatically.

Source: [CMakeWorkflow.Client/Toolchains/ToolchainService.cs](../CMakeWorkflow.Client/Toolchains/ToolchainService.cs#L8)

```csharp
public sealed class ToolchainService(CMakeWorkflow.Client.CMakeWorkflowClient client)
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Toolchains.ToolchainInventory> DiscoverAsync(System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Toolchains.ToolchainResolution> ResolveAsync(CMakeWorkflow.Client.Toolchains.ToolchainSelection selection, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Toolchains.DeveloperEnvironmentSnapshot> InspectEnvironmentAsync(CMakeWorkflow.Client.Toolchains.DeveloperEnvironmentRequest request, System.Threading.CancellationToken ct = default);
}
```

#### `VisualStudioInstance`

Source: [CMakeWorkflow.Client/Toolchains/VisualStudioInstance.cs](../CMakeWorkflow.Client/Toolchains/VisualStudioInstance.cs#L3)

```csharp
public sealed record VisualStudioInstance(string Id, string Name, string Version, string InstallationPath, string? DeveloperCommandPath);
```

### Namespace `CMakeWorkflow.Client.Transport`

#### `SshConnectionException`

SSH transport failed before a usable Host connection was established.

Source: [CMakeWorkflow.Client/Transport/SshConnectionException.cs](../CMakeWorkflow.Client/Transport/SshConnectionException.cs#L5)

```csharp
public sealed class SshConnectionException(string message, string diagnostics, int? exitCode, System.Exception inner) : System.IO.IOException
{
    public string Diagnostics { get; }
    public int? ExitCode { get; }
}
```

#### `SshHostOptions`

Connects to an independently running Linux Host via the installed OpenSSH client.

All remote paths refer to the remote filesystem. No files are synchronized.

Source: [CMakeWorkflow.Client/Transport/SshHostOptions.cs](../CMakeWorkflow.Client/Transport/SshHostOptions.cs#L5)

```csharp
public sealed record SshHostOptions(string Destination, string Endpoint, string Workspace)
{
    public string RemoteExecutable { get; init; }
    public string SshExecutable { get; init; }
    public System.TimeSpan ConnectionTimeout { get; init; }
}
```

#### `SshNetHostOptions`

A direct SSH.NET connection to a persistent Linux Host, without spawning OpenSSH. The caller must pin the server host
key and provide a private-key file.

Source: [CMakeWorkflow.Client/Transport/SshNetHostOptions.cs](../CMakeWorkflow.Client/Transport/SshNetHostOptions.cs#L7)

```csharp
public sealed record SshNetHostOptions(string Host, string Username, string PrivateKeyPath, string HostKeySha256, string Endpoint, string Workspace)
{
    public int Port { get; init; }
    public string RemoteExecutable { get; init; }
    public System.TimeSpan ConnectionTimeout { get; init; }
}
```

#### `SshNetLaunchOptions`

Launch a private, single-session Linux Host on an SSH.NET exec channel. Unlike a persistent Host connection, disposing
this client stops its Host and cancels any work still running there. It does not deploy the executable.

Source: [CMakeWorkflow.Client/Transport/SshNetLaunchOptions.cs](../CMakeWorkflow.Client/Transport/SshNetLaunchOptions.cs#L8)

```csharp
public sealed record SshNetLaunchOptions(string Host, string Username, string PrivateKeyPath, string HostKeySha256, string Workspace)
{
    public int Port { get; init; }
    public string RemoteExecutable { get; init; }
    public System.TimeSpan ConnectionTimeout { get; init; }
}
```

### Namespace `CMakeWorkflow.Client.Workflows`

#### `CancellationReceipt`

Result of a durable cancellation-intent mutation.

Requested=true is not proof of stopped workers or a terminal canceled outcome. Revision belongs to cancellation intent
and is independent of recovery reconciliation revisions.

Source: [CMakeWorkflow.Client/Workflows/CancellationReceipt.cs](../CMakeWorkflow.Client/Workflows/CancellationReceipt.cs#L12)

```csharp
public sealed record CancellationReceipt(string OperationId, string State, long Revision, bool Requested);
```

#### `EventPage`

Bounded operation journal page using an exclusive sequence cursor.

An empty page does not imply terminal state. The journal is audit evidence, not a recovery checkpoint.

Source: [CMakeWorkflow.Client/Workflows/EventPage.cs](../CMakeWorkflow.Client/Workflows/EventPage.cs#L15)

```csharp
public sealed record EventPage(string OperationId, long AfterSequence, long NextSequence, bool HasMore, System.Collections.Generic.List<System.Text.Json.JsonElement> Events);
```

#### `ExecutionService`

Domain-qualified execution facade borrowing the client connection.

Source: [CMakeWorkflow.Client/Workflows/ExecutionService.cs](../CMakeWorkflow.Client/Workflows/ExecutionService.cs#L5)

```csharp
public sealed class ExecutionService(CMakeWorkflow.Client.CMakeWorkflowClient client)
{
    public System.Threading.Tasks.Task<System.Text.Json.JsonElement> GetReportAsync(string operationId, System.Threading.CancellationToken cancellationToken = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.WorkflowOperation> StartAsync(CMakeWorkflow.Client.Workflows.ExecutionStartOptions options, System.Threading.CancellationToken cancellationToken = default);
}
```

#### `ExecutionStartOptions`

Domain-qualified durable admission parameters, interpreted on the Host.

Paths and hashes refer to the Host filesystem; the SDK sends them unchanged. Save ClientRequestId before submission.
Acceptance is not proof of dispatch or completion.

Source: [CMakeWorkflow.Client/Workflows/ExecutionStartOptions.cs](../CMakeWorkflow.Client/Workflows/ExecutionStartOptions.cs#L15)

```csharp
public sealed record ExecutionStartOptions(string Domain, string ClientRequestId, string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);
```

#### `OperationArtifactChunk`

One byte range; callers must verify the assembled file against the catalog SHA-256.

Source: [CMakeWorkflow.Client/Workflows/OperationArtifactContracts.cs](../CMakeWorkflow.Client/Workflows/OperationArtifactContracts.cs#L44)

```csharp
public sealed record OperationArtifactChunk(string ArtifactId, long Offset, long TotalSize, long LastWriteUtcTicks, byte[] Content, bool Eof);
```

#### `OperationArtifactInfo`

Finalized file evidence; artifact IDs are opaque and scoped to the operation.

Source: [CMakeWorkflow.Client/Workflows/OperationArtifactContracts.cs](../CMakeWorkflow.Client/Workflows/OperationArtifactContracts.cs#L26)

```csharp
public sealed record OperationArtifactInfo(string ArtifactId, string Kind, string? NodeId, string RelativePath, long Size, long LastWriteUtcTicks, string Sha256);
```

#### `OperationArtifactList`

Catalog of verified finalized run files.

Source: [CMakeWorkflow.Client/Workflows/OperationArtifactContracts.cs](../CMakeWorkflow.Client/Workflows/OperationArtifactContracts.cs#L33)

```csharp
public sealed record OperationArtifactList(string OperationId, string ReportSha256, System.Collections.Generic.List<CMakeWorkflow.Client.Workflows.OperationArtifactInfo> Artifacts);
```

#### `OperationNodeView`

Verified terminal node evidence; fields may be unavailable for nodes that never executed.

Source: [CMakeWorkflow.Client/Workflows/OperationOverview.cs](../CMakeWorkflow.Client/Workflows/OperationOverview.cs#L39)

```csharp
public sealed record OperationNodeView(string Id, string Kind, string Status, System.Collections.Generic.List<string> Dependencies, System.DateTimeOffset? StartedAt, System.DateTimeOffset? FinishedAt, int? ExitCode, string? Error, string? Stdout, string? Stderr);
```

#### `OperationOverview`

Read-only domain-neutral projection backed by verified terminal evidence.

Before finalization, Nodes is empty and report-derived nullable fields are null; this does not mean the DAG is empty.
Even finalized reports may omit domain-specific environment/timing details.

Source: [CMakeWorkflow.Client/Workflows/OperationOverview.cs](../CMakeWorkflow.Client/Workflows/OperationOverview.cs#L20)

```csharp
public sealed record OperationOverview(int SchemaVersion, CMakeWorkflow.Client.Workflows.OperationSnapshot Operation, bool IsFinalized, string? RunDirectory, string? ReportSha256, System.DateTimeOffset? StartedAt, System.DateTimeOffset? FinishedAt, string? Error, string? HostId, string? EnvironmentFingerprint, System.Collections.Generic.List<CMakeWorkflow.Client.Workflows.OperationNodeView> Nodes);
```

#### `OperationPage`

Bounded page of durable operations, newest first.

Source: [CMakeWorkflow.Client/Workflows/OperationQuery.cs](../CMakeWorkflow.Client/Workflows/OperationQuery.cs#L19)

```csharp
public sealed record OperationPage(int SchemaVersion, System.Collections.Generic.List<CMakeWorkflow.Client.Workflows.OperationSnapshot> Items, string? NextCursor);
```

#### `OperationQuery`

Workspace operation filter and keyset cursor.

A cursor excludes later admissions, but state transitions are not snapshot-isolated.

Source: [CMakeWorkflow.Client/Workflows/OperationQuery.cs](../CMakeWorkflow.Client/Workflows/OperationQuery.cs#L12)

```csharp
public sealed record OperationQuery(int Limit = 50, string? State = null, string? Domain = null, string? Cursor = null);
```

#### `OperationReportSnapshot`

Verified operation-bound terminal CMake report.

Source: [CMakeWorkflow.Client/Workflows/OperationReportSnapshot.cs](../CMakeWorkflow.Client/Workflows/OperationReportSnapshot.cs#L10)

```csharp
public sealed record OperationReportSnapshot(int SchemaVersion, string RunDirectory, string Revision, System.Text.Json.JsonElement Report);
```

#### `OperationService`

Queries durable operations in the initialized Host workspace.

Handles borrow the client and do not own remote execution. Calls do not automatically retry or reconnect.

Source: [CMakeWorkflow.Client/Workflows/OperationService.cs](../CMakeWorkflow.Client/Workflows/OperationService.cs#L7)

```csharp
public sealed class OperationService(CMakeWorkflow.Client.CMakeWorkflowClient client)
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.OperationPage> ListAsync(CMakeWorkflow.Client.Workflows.OperationQuery? query = null, System.Threading.CancellationToken ct = default);
    public CMakeWorkflow.Client.Workflows.WorkflowOperation GetHandle(string operationId);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.OperationOverview> GetOverviewAsync(string operationId, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.OperationSnapshot> GetAsync(string operationId, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.OperationSnapshot> FindAsync(string clientRequestId, System.Threading.CancellationToken ct = default);
}
```

#### `OperationSnapshot`

Durable operation state projection, not a live worker snapshot.

Terminal states are succeeded, failed and canceled. cancel_requested records intent only. accepted_not_dispatched and
dispatch_authorized describe admission; authorized does not prove a process started.

Source: [CMakeWorkflow.Client/Workflows/OperationSnapshot.cs](../CMakeWorkflow.Client/Workflows/OperationSnapshot.cs#L15)

```csharp
public sealed record OperationSnapshot(string WorkspaceId, string OperationId, string ClientRequestId, string Kind, string State, string ManifestSha256, string ManifestBaseDirectory);
```

#### `PrepareTargetOptions`

Source: [CMakeWorkflow.Client/Workflows/PrepareTargetOptions.cs](../CMakeWorkflow.Client/Workflows/PrepareTargetOptions.cs#L5)

```csharp
public sealed record PrepareTargetOptions(string RequestId, string ResultsDirectory, string Action, string BuildDirectory, string? Target = null, string? SecondTarget = null, string? Configuration = null, string[]? Arguments = null, string[]? SecondArguments = null, CMakeWorkflow.Contracts.Workflows.WorkflowToolchainBinding? Toolchain = null);
```

#### `PreparedWorkflow`

Host-side manifest preparation result; no operation has been started.

Source: [CMakeWorkflow.Client/Workflows/PreparedWorkflow.cs](../CMakeWorkflow.Client/Workflows/PreparedWorkflow.cs#L10)

```csharp
public sealed record PreparedWorkflow(int SchemaVersion, string Manifest, string ManifestSha256, string ResultsDirectory);
```

#### `RecoverySnapshot`

Durable recovery evidence and operator decision for an operation.

Null fields indicate evidence or decisions not recorded; they do not prove absence of external effects. Recovery
revision is separate from cancellation-intent revision.

Source: [CMakeWorkflow.Client/Workflows/RecoverySnapshot.cs](../CMakeWorkflow.Client/Workflows/RecoverySnapshot.cs#L17)

```csharp
public sealed record RecoverySnapshot(string OperationId, string AcceptanceState, string? TerminalState, bool CancellationRequested, string? RunDirectory, string? CheckpointSha256, string? Decision, long Revision, string? Note);
```

#### `StartWorkflowOptions`

Immutable durable workflow admission inputs interpreted on the connected Host.

Identical normalized request identities under one ID return the same acceptance; conflicting reuse fails. Cancellation
or a missing reply does not establish rejection. Persist the ID and find it in the same workspace.

Source: [CMakeWorkflow.Client/Workflows/StartWorkflowOptions.cs](../CMakeWorkflow.Client/Workflows/StartWorkflowOptions.cs#L13)

```csharp
public sealed record StartWorkflowOptions(string ClientRequestId, string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);
```

#### `TargetService`

Prepares Host target workflows without admitting execution; borrows the client lifetime.

Source: [CMakeWorkflow.Client/Workflows/TargetService.cs](../CMakeWorkflow.Client/Workflows/TargetService.cs#L4)

```csharp
public sealed class TargetService(CMakeWorkflow.Client.CMakeWorkflowClient client)
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.PreparedWorkflow> PrepareAsync(CMakeWorkflow.Client.Workflows.PrepareTargetOptions options, System.Threading.CancellationToken ct = default);
}
```

#### `WorkflowEvent`

Source: [CMakeWorkflow.Client/Workflows/WorkflowEvent.cs](../CMakeWorkflow.Client/Workflows/WorkflowEvent.cs#L5)

```csharp
public sealed record WorkflowEvent(long Sequence, string Kind, System.Text.Json.JsonElement Data);
```

#### `WorkflowOperation`

Non-owning durable operation handle bound to a client connection.

Creating or abandoning a handle has no remote effect. Dispose/reconnect the client separately. All tokens cancel
individual RPCs, polling or local transfer work; they do not request durable operation cancellation. Use
RequestCancellationAsync for that separate mutation. No method implicitly reconnects or resubmits a workflow.

Source: [CMakeWorkflow.Client/Workflows/WorkflowOperation.cs](../CMakeWorkflow.Client/Workflows/WorkflowOperation.cs#L11)

```csharp
public sealed class WorkflowOperation(CMakeWorkflow.Client.CMakeWorkflowClient client, string operationId)
{
    public string OperationId { get; }
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.OperationSnapshot> GetSnapshotAsync(System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.OperationOverview> GetOverviewAsync(System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.OperationReportSnapshot> GetReportAsync(System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.OperationArtifactList> ListArtifactsAsync(System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.OperationArtifactChunk> ReadArtifactChunkAsync(CMakeWorkflow.Client.Workflows.OperationArtifactInfo artifact, long offset, int count = 65536, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<string> DownloadArtifactAsync(CMakeWorkflow.Client.Workflows.OperationArtifactInfo artifact, string destinationPath, System.IProgress<long>? progress = null, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.CancellationReceipt> RequestCancellationAsync(long expectedRevision, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.RecoverySnapshot> InspectRecoveryAsync(System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.RecoverySnapshot> ReconcileAsync(long revision, string decision, string note, bool confirmedQuiescent, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.RecoverySnapshot> AdmitResumeAsync(long revision, bool confirmedQuiescent, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.RecoverySnapshot> ExecuteResumeAsync(long revision, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.EventPage> ReadEventsAsync(long afterSequence = 0, int limit = 100, System.Threading.CancellationToken ct = default);
    public System.Collections.Generic.IAsyncEnumerable<System.Text.Json.JsonElement> WatchEventsAsync(long afterSequence = 0, System.TimeSpan? pollInterval = null, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.OperationSnapshot> WaitForCompletionAsync(System.TimeSpan? pollInterval = null, System.Threading.CancellationToken ct = default);
}
```

#### `WorkflowPreflightDiagnostic`

One planner diagnostic; does not establish future runtime availability.

Source: [CMakeWorkflow.Client/Workflows/WorkflowPreflight.cs](../CMakeWorkflow.Client/Workflows/WorkflowPreflight.cs#L19)

```csharp
public sealed record WorkflowPreflightDiagnostic(string Severity, string Message);
```

#### `WorkflowPreflightNode`

Observed prospective node and its admission diagnostics.

Source: [CMakeWorkflow.Client/Workflows/WorkflowPreflight.cs](../CMakeWorkflow.Client/Workflows/WorkflowPreflight.cs#L32)

```csharp
public sealed record WorkflowPreflightNode(string Id, string Kind, string Target, string BuildDirectory, string? Configuration, System.Collections.Generic.List<string> Dependencies, System.Collections.Generic.List<string> Resources, bool Exclusive, string ObservedState, System.Collections.Generic.List<CMakeWorkflow.Client.Workflows.WorkflowPreflightDiagnostic> Diagnostics);
```

#### `WorkflowPreflightOptions`

Read-only preflight inputs using exact bytes on the connected Host.

Passing preflight does not reserve trees or authorize dispatch. Explicit durable start rechecks manifest identity.
Canceling this read does not create or cancel a workflow operation.

Source: [CMakeWorkflow.Client/Workflows/WorkflowPreflight.cs](../CMakeWorkflow.Client/Workflows/WorkflowPreflight.cs#L13)

```csharp
public sealed record WorkflowPreflightOptions(string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);
```

#### `WorkflowPreflightResult`

Read-only preflight evidence suitable for explicit start request construction.

Valid reflects this observation only. The result is neither execution admission nor a reservation. Changes in manifest
bytes or Host environment may invalidate later admission.

Source: [CMakeWorkflow.Client/Workflows/WorkflowPreflight.cs](../CMakeWorkflow.Client/Workflows/WorkflowPreflight.cs#L53)

```csharp
public sealed record WorkflowPreflightResult(int SchemaVersion, string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory, bool Valid, System.Collections.Generic.List<CMakeWorkflow.Client.Workflows.WorkflowPreflightNode> Nodes)
{
    public CMakeWorkflow.Client.Workflows.StartWorkflowOptions CreateStartOptions(string clientRequestId);
}
```

#### `WorkflowService`

Workflow planning and admission facade borrowing the client connection.

Paths are Host filesystem paths and are sent unchanged, including over SSH. Calls do not automatically reconnect or
retry. Preflight is advisory; start rechecks admission requirements.

Source: [CMakeWorkflow.Client/Workflows/WorkflowService.cs](../CMakeWorkflow.Client/Workflows/WorkflowService.cs#L12)

```csharp
public sealed class WorkflowService(CMakeWorkflow.Client.CMakeWorkflowClient client)
{
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.WorkflowOperation> StartAsync(CMakeWorkflow.Client.Workflows.StartWorkflowOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.WorkflowPreflightResult> PreflightAsync(CMakeWorkflow.Client.Workflows.WorkflowPreflightOptions options, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.WorkflowPreflightResult> PreflightAsync(CMakeWorkflow.Client.Recipes.PreparedRecipe prepared, int jobs, string resultsDirectory, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.WorkflowPreflightResult> PreflightAsync(CMakeWorkflow.Client.Recipes.PreparedBenchmark prepared, int jobs, string resultsDirectory, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<CMakeWorkflow.Client.Workflows.WorkflowPreflightResult> PreflightAsync(CMakeWorkflow.Client.Workflows.PreparedWorkflow prepared, int jobs, System.Threading.CancellationToken ct = default);
    public System.Threading.Tasks.Task<System.Text.Json.JsonElement> PlanAsync(string manifest, System.Threading.CancellationToken ct = default);
}
```

## CMakeWorkflow.Contracts

### Namespace `CMakeWorkflow.Contracts.Builders`

#### `BuildNodeRef`

A typed reference usable as the source of a run or profile node.

Source: [CMakeWorkflow.Contracts/Builders/BuildNodeRef.cs](../CMakeWorkflow.Contracts/Builders/BuildNodeRef.cs#L8)

```csharp
public readonly record struct BuildNodeRef(string Id)
{
    public static implicit operator CMakeWorkflow.Contracts.Builders.WorkflowNodeRef(CMakeWorkflow.Contracts.Builders.BuildNodeRef value);
}
```

#### `WorkflowBuilder`

Builds schema-v2 workflow documents in memory. Never invokes CMake, the Host, or the filesystem. The planner remains
authoritative for target metadata, paths and execution admission.

Source: [CMakeWorkflow.Contracts/Builders/WorkflowBuilder.cs](../CMakeWorkflow.Contracts/Builders/WorkflowBuilder.cs#L11)

```csharp
public sealed class WorkflowBuilder
{
    public static CMakeWorkflow.Contracts.Builders.WorkflowBuilder Create();
    public CMakeWorkflow.Contracts.Builders.WorkflowBuilder Context(string name, System.Action<CMakeWorkflow.Contracts.Builders.WorkflowContextBuilder> configure);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeRef Inspect(string id, string context, System.Action<CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder>? configure = null);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeRef Configure(string id, string context, System.Action<CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder>? configure = null);
    public CMakeWorkflow.Contracts.Builders.BuildNodeRef BuildTarget(string id, string context, string target, System.Action<CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder>? configure = null);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeRef Invoke(string id, string context, string target, System.Action<CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder>? configure = null);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeRef Run(string id, CMakeWorkflow.Contracts.Builders.BuildNodeRef from, System.Action<CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder>? configure = null);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeRef Profile(string id, CMakeWorkflow.Contracts.Builders.BuildNodeRef from, System.Action<CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder>? configure = null);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeRef CTest(string id, string context, CMakeWorkflow.Contracts.Builders.BuildNodeRef afterBuild, System.Action<CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder>? configure = null);
    public CMakeWorkflow.Contracts.Workflows.WorkflowDocument ToDocument();
}
```

#### `WorkflowContextBuilder`

Mutable fluent adapter over a workflow DTO.

Methods only modify the supplied DTO and return this builder. They perform no execution or persistence. The caller owns
the DTO; planning remains authoritative for semantic validation.

Source: [CMakeWorkflow.Contracts/Builders/WorkflowContextBuilder.cs](../CMakeWorkflow.Contracts/Builders/WorkflowContextBuilder.cs#L12)

```csharp
public sealed class WorkflowContextBuilder(CMakeWorkflow.Contracts.Workflows.WorkflowContext context)
{
    public CMakeWorkflow.Contracts.Builders.WorkflowContextBuilder BuildDirectory(string path);
    public CMakeWorkflow.Contracts.Builders.WorkflowContextBuilder SourceDirectory(string path);
    public CMakeWorkflow.Contracts.Builders.WorkflowContextBuilder Configuration(string name);
    public CMakeWorkflow.Contracts.Builders.WorkflowContextBuilder ConfigurePreset(string name);
    public CMakeWorkflow.Contracts.Builders.WorkflowContextBuilder Generator(string name);
    public CMakeWorkflow.Contracts.Builders.WorkflowContextBuilder AdoptExisting();
    public CMakeWorkflow.Contracts.Builders.WorkflowContextBuilder Managed();
    public CMakeWorkflow.Contracts.Builders.WorkflowContextBuilder Observe();
    public CMakeWorkflow.Contracts.Builders.WorkflowContextBuilder AllowUnverified();
    public CMakeWorkflow.Contracts.Builders.WorkflowContextBuilder Toolchain(CMakeWorkflow.Contracts.Workflows.WorkflowToolchainBinding binding);
}
```

#### `WorkflowNodeBuilder`

Mutable fluent adapter over a workflow DTO.

Methods only modify the supplied DTO and return this builder. They perform no execution or persistence. The caller owns
the DTO; planning remains authoritative for semantic validation.

Source: [CMakeWorkflow.Contracts/Builders/WorkflowNodeBuilder.cs](../CMakeWorkflow.Contracts/Builders/WorkflowNodeBuilder.cs#L12)

```csharp
public sealed class WorkflowNodeBuilder(CMakeWorkflow.Contracts.Workflows.WorkflowNode node, CMakeWorkflow.Contracts.Builders.WorkflowBuilder owner)
{
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder DependsOn(params CMakeWorkflow.Contracts.Builders.WorkflowNodeRef[] prerequisites);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder Arguments(params string[] values);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder Timeout(System.TimeSpan value);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder Resource(string name);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder Exclusive();
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder WorkingDirectory(string path);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder Environment(string name, string value);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder Option(string name, string value);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder HarnessDirectory(string path);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder TestRegex(string regex);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder LabelRegex(string regex);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder RequireCpuSet(string cpuSet);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder RequireProfiler(string backend, string mode, string? eventSource = null);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder Input(string path, string? sha256 = null, bool snapshot = false);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder InputFrom(CMakeWorkflow.Contracts.Builders.WorkflowNodeRef producer, string artifact, string? path = null);
    public CMakeWorkflow.Contracts.Builders.WorkflowNodeBuilder Output(string name, string path, string root = "manifest", bool fresh = false);
}
```

#### `WorkflowNodeRef`

A reference to a previously declared node. It is not a running operation.

Source: [CMakeWorkflow.Contracts/Builders/WorkflowNodeRef.cs](../CMakeWorkflow.Contracts/Builders/WorkflowNodeRef.cs#L6)

```csharp
public readonly record struct WorkflowNodeRef(string Id);
```

### Namespace `CMakeWorkflow.Contracts.Recipes`

#### `BenchmarkAxisV1`

One argument axis in a Cartesian benchmark matrix.

Names and flags must be unique in the recipe. Each value contributes a literal flag/value pair. The DTO itself performs
no I/O or validation; recipe builders enforce these requirements.

Source: [CMakeWorkflow.Contracts/Recipes/BenchmarkAxisV1.cs](../CMakeWorkflow.Contracts/Recipes/BenchmarkAxisV1.cs#L15)

```csharp
public sealed record BenchmarkAxisV1(string Name, string Flag, string[] Values);
```

#### `BenchmarkMatrixRecipeV1`

Cartesian argument matrix with serialized timing runs after all variant builds.

Kind is benchmark-matrix. Requires 1..32 distinct variants, 0..8 axes, repeats 1..1000, and at most 4096 nodes including
builds. ExecutionOrder is sequential or round-robin; round-robin reverses variant order on even repetitions. The DTO
itself performs no I/O or validation; recipe builders enforce these requirements.

Source: [CMakeWorkflow.Contracts/Recipes/BenchmarkMatrixRecipeV1.cs](../CMakeWorkflow.Contracts/Recipes/BenchmarkMatrixRecipeV1.cs#L22)

```csharp
public sealed record BenchmarkMatrixRecipeV1(string Kind, int SchemaVersion, CMakeWorkflow.Contracts.Recipes.BuildVariantV1[] Variants, string Target, int Repeats, double TimeoutSeconds, string[] FixedArguments, CMakeWorkflow.Contracts.Recipes.BenchmarkAxisV1[] Axes, string ExecutionOrder);
```

#### `BenchmarkRecipeV1`

Build followed by a serial chain of timing runs.

Kind is benchmark. Repeats is 1..1000. The existing build tree is adopted with allowUnverified provenance. The DTO
itself performs no I/O or validation; recipe builders enforce these requirements.

Source: [CMakeWorkflow.Contracts/Recipes/BenchmarkRecipeV1.cs](../CMakeWorkflow.Contracts/Recipes/BenchmarkRecipeV1.cs#L22)

```csharp
public sealed record BenchmarkRecipeV1(string Kind, int SchemaVersion, string BuildDirectory, string Target, string? Configuration, int Repeats, double TimeoutSeconds, string[] Arguments);
```

#### `BuildMatrixRecipeV1`

Build every target in every variant without adding artificial ordering dependencies.

Kind is build-matrix. Requires 1..32 unique variants with distinct build trees, unique targets, and at most 512 expanded
nodes. The DTO itself performs no I/O or validation; recipe builders enforce these requirements.

Source: [CMakeWorkflow.Contracts/Recipes/BuildMatrixRecipeV1.cs](../CMakeWorkflow.Contracts/Recipes/BuildMatrixRecipeV1.cs#L16)

```csharp
public sealed record BuildMatrixRecipeV1(string Kind, int SchemaVersion, CMakeWorkflow.Contracts.Recipes.BuildVariantV1[] Variants, string[] Targets);
```

#### `BuildVariantV1`

Named build-tree variant used by matrix recipes.

Variant names match [a-z][a-z0-9_]* and must be unique within the recipe. The DTO itself performs no I/O or validation;
recipe builders enforce these requirements.

Source: [CMakeWorkflow.Contracts/Recipes/BuildVariantV1.cs](../CMakeWorkflow.Contracts/Recipes/BuildVariantV1.cs#L15)

```csharp
public sealed record BuildVariantV1(string Name, string BuildDirectory, string? Configuration = null);
```

#### `ProfileRecipeV1`

Build and time variants, then capture profiler evidence.

Kind is profile. Requires 1..32 variants and 1..100 repetitions. Timing runs wait for all builds and are serialized. A
configure preset requires exactly one variant and an absolute source directory; otherwise existing trees are adopted.
The DTO itself performs no I/O or validation; recipe builders enforce these requirements.

Source: [CMakeWorkflow.Contracts/Recipes/ProfileRecipeV1.cs](../CMakeWorkflow.Contracts/Recipes/ProfileRecipeV1.cs#L26)

```csharp
public sealed record ProfileRecipeV1(string Kind, int SchemaVersion, CMakeWorkflow.Contracts.Recipes.ProfileVariant[] Variants, string Target, string? Configuration, int Repeats, double TimeoutSeconds, string[] Arguments, string Backend, string Mode, string Platform, string? SourceDirectory = null, string? ConfigurePreset = null);
```

#### `ProfileVariant`

Named build-tree variant used for timing and profile capture.

Names match [a-z][a-z0-9_]*; names and normalized build directories must be distinct in a profile recipe. The DTO itself
performs no I/O or validation; recipe builders enforce these requirements.

Source: [CMakeWorkflow.Contracts/Recipes/ProfileVariant.cs](../CMakeWorkflow.Contracts/Recipes/ProfileVariant.cs#L14)

```csharp
public sealed record ProfileVariant(string Name, string BuildDirectory);
```

#### `TestMatrixGroupV1`

Named CTest selection with exactly one non-null filter.

The selected regex must be nonempty, contain no NUL, and pass regex validation. The DTO itself performs no I/O or
validation; recipe builders enforce these requirements.

Source: [CMakeWorkflow.Contracts/Recipes/TestMatrixGroupV1.cs](../CMakeWorkflow.Contracts/Recipes/TestMatrixGroupV1.cs#L15)

```csharp
public sealed record TestMatrixGroupV1(string Name, string? TestRegex = null, string? LabelRegex = null);
```

#### `TestMatrixRecipeV1`

Build once, then execute filtered CTest groups.

Kind is test-matrix. Requires 1..64 uniquely named groups. Parallel groups depend on the build; sequential groups form a
chain. The DTO itself performs no I/O or validation; recipe builders enforce these requirements.

Source: [CMakeWorkflow.Contracts/Recipes/TestMatrixRecipeV1.cs](../CMakeWorkflow.Contracts/Recipes/TestMatrixRecipeV1.cs#L20)

```csharp
public sealed record TestMatrixRecipeV1(string Kind, int SchemaVersion, string BuildDirectory, string Target, string? Configuration, CMakeWorkflow.Contracts.Recipes.TestMatrixGroupV1[] Groups, string ExecutionOrder, double TimeoutSeconds);
```

### Namespace `CMakeWorkflow.Contracts.Workflows`

#### `WorkflowArtifactInput`

Input evidence from a filesystem path or a named producer output.

At least Path or From must be supplied. From requires Artifact and adds a DAG dependency. Artifact without From is
invalid. Evidence is checked before execution; an optional hash constrains expected bytes.

Source: [CMakeWorkflow.Contracts/Workflows/WorkflowArtifactInput.cs](../CMakeWorkflow.Contracts/Workflows/WorkflowArtifactInput.cs#L10)

```csharp
public sealed class WorkflowArtifactInput
{
    public string? Path { get; set; }
    public string? Sha256 { get; set; }
    public string? From { get; set; }
    public string? Artifact { get; set; }
    public bool Snapshot { get; set; }
}
```

#### `WorkflowArtifactOutput`

Named output evidence captured after a node succeeds.

Names must be unique within the producer. Inspect nodes cannot declare outputs. A declaration records and validates
files; it does not make an external command transactional.

Source: [CMakeWorkflow.Contracts/Workflows/WorkflowArtifactOutput.cs](../CMakeWorkflow.Contracts/Workflows/WorkflowArtifactOutput.cs#L10)

```csharp
public sealed class WorkflowArtifactOutput
{
    public string Name { get; set; }
    public string Path { get; set; }
    public string Root { get; set; }
    public bool Fresh { get; set; }
}
```

#### `WorkflowContext`

Requested CMake build-tree identity and ownership policy.

A context describes one native build tree. Planning observes its current state; this DTO does not configure it. Managed
mode permits explicit configure nodes; adopt mode accepts an existing tree under the provenance policy. Null optional
fields mean no explicit request or expectation, rather than an empty string.

Source: [CMakeWorkflow.Contracts/Workflows/WorkflowContext.cs](../CMakeWorkflow.Contracts/Workflows/WorkflowContext.cs#L11)

```csharp
public sealed class WorkflowContext
{
    public string BuildDirectory { get; set; }
    public string? Configuration { get; set; }
    public string? ConfigurePreset { get; set; }
    public string? SourceDirectory { get; set; }
    public string? Mode { get; set; }
    public string? Generator { get; set; }
    public string ProvenancePolicy { get; set; }
    public CMakeWorkflow.Contracts.Workflows.WorkflowToolchainBinding? Toolchain { get; set; }
}
```

#### `WorkflowDocument`

Mutable serialized workflow definition; construction alone does not validate or execute it.

Schema versions 1 and 2 are supported. Context keys and node IDs are case-sensitive. The planner validates the whole
DAG, including unselected branches; dependencies must exist and be acyclic. Collections and their elements must remain
non-null. Paths are interpreted on the execution host.

Source: [CMakeWorkflow.Contracts/Workflows/WorkflowDocument.cs](../CMakeWorkflow.Contracts/Workflows/WorkflowDocument.cs#L13)

```csharp
public sealed class WorkflowDocument
{
    public int SchemaVersion { get; set; }
    public System.Collections.Generic.Dictionary<string, CMakeWorkflow.Contracts.Workflows.WorkflowContext> Contexts { get; set; }
    public System.Collections.Generic.List<CMakeWorkflow.Contracts.Workflows.WorkflowNode> Nodes { get; set; }
}
```

#### `WorkflowExecutionConstraints`

Optional execution requirements checked before external dispatch.

CPU affinity applies only to run/profile nodes; profiler requirements apply only to profile nodes. Null means no
corresponding requirement. Inspect nodes cannot request execution constraints.

Source: [CMakeWorkflow.Contracts/Workflows/WorkflowExecutionConstraints.cs](../CMakeWorkflow.Contracts/Workflows/WorkflowExecutionConstraints.cs#L10)

```csharp
public sealed class WorkflowExecutionConstraints
{
    public string? CpuSet { get; set; }
    public CMakeWorkflow.Contracts.Workflows.WorkflowProfilerRequirement? Profiler { get; set; }
}
```

#### `WorkflowNode`

One DAG node with kind-specific execution and evidence requirements.

Kinds are inspect, configure, build, invoke, run, profile, profile_snapshot and ctest. IDs must be unique. Run/profile
inherit context and target from a build node through From. `profile_snapshot` requires schema version 2 and pins an
executable by absolute path and SHA-256. DependsOn, From and producer artifact references
contribute dependencies. A node runs only after its prerequisites succeed. Collections must be non-null; construction
does not validate kind-specific combinations. After interruption, a persisted running intent is uncertain evidence and
must not be blindly re-executed.

Source: [CMakeWorkflow.Contracts/Workflows/WorkflowNode.cs](../CMakeWorkflow.Contracts/Workflows/WorkflowNode.cs#L15)

```csharp
public sealed class WorkflowNode
{
    public string Id { get; set; }
    public string Kind { get; set; }
    public string? Context { get; set; }
    public string? Target { get; set; }
    public string? From { get; set; }
    public string? SnapshotExecutable { get; set; }
    public string? SnapshotSha256 { get; set; }
    public System.Collections.Generic.List<string> DependsOn { get; set; }
    public System.Collections.Generic.List<string> Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public System.Collections.Generic.Dictionary<string, string> Environment { get; set; }
    public System.Collections.Generic.Dictionary<string, string> Options { get; set; }
    public string? HarnessDirectory { get; set; }
    public string? TestRegex { get; set; }
    public string? LabelRegex { get; set; }
    public System.Collections.Generic.List<string> Resources { get; set; }
    public bool Exclusive { get; set; }
    public double? TimeoutSeconds { get; set; }
    public System.Collections.Generic.List<CMakeWorkflow.Contracts.Workflows.WorkflowArtifactInput> Inputs { get; set; }
    public System.Collections.Generic.List<CMakeWorkflow.Contracts.Workflows.WorkflowArtifactOutput> Outputs { get; set; }
    public CMakeWorkflow.Contracts.Workflows.WorkflowExecutionConstraints? Constraints { get; set; }
}
```

#### `WorkflowProfilerRequirement`

Explicit profiler capability requirement for a profile node.

Backend and Mode must match the node options; backend auto is not a concrete requirement. The profiling extension must
verify the requested capability before capture.

Source: [CMakeWorkflow.Contracts/Workflows/WorkflowProfilerRequirement.cs](../CMakeWorkflow.Contracts/Workflows/WorkflowProfilerRequirement.cs#L10)

```csharp
public sealed class WorkflowProfilerRequirement
{
    public string Backend { get; set; }
    public string Mode { get; set; }
    public string? EventSource { get; set; }
}
```

#### `WorkflowToolchainBinding`

Host-local toolchain identity requests; candidate IDs are never executable paths.

The execution host resolves IDs afresh and validates build-tree identity before dispatch. Null candidate/profile IDs
leave that selection unspecified.

Source: [CMakeWorkflow.Contracts/Workflows/WorkflowToolchainBinding.cs](../CMakeWorkflow.Contracts/Workflows/WorkflowToolchainBinding.cs#L22)

```csharp
public sealed record WorkflowToolchainBinding(string? CMakeCandidateId = null, string? NinjaCandidateId = null, string? CompilerCandidateId = null, string? SdkCandidateId = null, string? EnvironmentProfileId = null, string Generator = "Ninja", string HostArchitecture = "x64", string TargetArchitecture = "x64");
```

