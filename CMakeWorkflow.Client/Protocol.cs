using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record ClientOptions(string Endpoint, string Workspace);
public sealed record LaunchOptions(string Executable, string Workspace, string? Endpoint = null);
public sealed record HostIdentity(int ProtocolVersion, int ProcessId, string WorkspaceDirectory, string[] Capabilities, JsonElement Profiles);
public sealed record StartWorkflowOptions(string ClientRequestId, string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);
public sealed record OperationSnapshot(string WorkspaceId, string OperationId, string ClientRequestId, string Kind, string State, string ManifestSha256, string ManifestBaseDirectory);
public sealed record CancellationReceipt(string OperationId, string State, long Revision, bool Requested);
public sealed record RecoverySnapshot(string OperationId, string AcceptanceState, string? TerminalState, bool CancellationRequested,
    string? RunDirectory, string? CheckpointSha256, string? Decision, long Revision, string? Note);
public sealed record WorkflowEvent(long Sequence, string Kind, JsonElement Data);
public sealed record EventPage(string OperationId, long AfterSequence, long NextSequence, bool HasMore, List<JsonElement> Events);
public sealed record RpcErrorData(string Code, string Message);
// Independent SDK models: the client references only Contracts, never Host.
public sealed record ToolchainCandidate(string Id, string Role, string Flavor, string Provider,
    string Path, string? Version = null, string? VisualStudioInstanceId = null,
    string? EnvironmentProfileId = null);
public sealed record VisualStudioInstance(string Id, string Name, string Version,
    string InstallationPath, string? DeveloperCommandPath);
public sealed record ToolchainInventory(int SchemaVersion, string Platform,
    List<VisualStudioInstance> VisualStudio, List<ToolchainCandidate> Candidates,
    List<string> Diagnostics);
public sealed record ToolchainSelection(
    [property: JsonPropertyName("cmakeCandidateId")] string? CMakeCandidateId = null,
    string? NinjaCandidateId = null,
    string? CompilerCandidateId = null, string? SdkCandidateId = null,
    string? EnvironmentProfileId = null, string Generator = "Ninja");
public sealed record ToolchainResolution(int SchemaVersion, string Status,
    ToolchainCandidate? CMake, ToolchainCandidate? Ninja, ToolchainCandidate? Compiler,
    ToolchainCandidate? Sdk, string? EnvironmentProfileId, List<string> Diagnostics);
public sealed record DeveloperEnvironmentRequest(string EnvironmentProfileId, string HostArchitecture = "x64",
    string TargetArchitecture = "x64", string? WindowsSdkVersion = null, string? ToolsetVersion = null);
public sealed record DeveloperEnvironmentSnapshot(int SchemaVersion, string EnvironmentProfileId,
    string HostArchitecture, string TargetArchitecture, string Fingerprint,
    Dictionary<string, string> VisibleVariables, List<string> Diagnostics);
internal sealed record ToolchainDiscoverRequest();
internal sealed record ToolchainResolveRequest(ToolchainSelection Selection);
internal sealed record ToolchainInspectEnvironmentRequest(DeveloperEnvironmentRequest Environment);
internal sealed record InitRequest(int ProtocolVersion, string WorkspaceDirectory);
internal sealed record OpIdRequest(string OperationId);
internal sealed record FindRequest(string ClientRequestId);
internal sealed record CancelRequest(string OperationId, long ExpectedRevision);
internal sealed record EventsRequest(string OperationId, long AfterSequence, int Limit);
internal sealed record ResumeRequest(string OperationId, long ExpectedRevision, bool ConfirmedQuiescent);
internal sealed record ReconcileRequest(string OperationId, long ExpectedRevision, string Decision, string Note, bool ConfirmedQuiescent);
internal sealed record PlanRequest(string Manifest, string[]? Only = null, string? ControllerBuildDirectory = null);

// Public .NET SDK DTOs outside the shared recipe/model contracts.
// TypeScript uses the same JSON field names.
public sealed record PreparedWorkflow(int SchemaVersion, string Manifest, string ManifestSha256,
    string ResultsDirectory);
public sealed record ProfileVariantOptions(string Name, string BuildDirectory,
    WorkflowToolchainBinding? Toolchain = null);
public sealed record PrepareProfileOptions(string RequestId, string ResultsDirectory, string Target,
    List<ProfileVariantOptions> Variants, int Repeats = 3, double TimeoutSeconds = 300,
    string Backend = "auto", string Mode = "cpu", string? Configuration = null,
    string? SourceDirectory = null, string? ConfigurePreset = null, string[]? Arguments = null);
public sealed record PrepareBenchmarkOptions(string RequestId, string OutputPath, BenchmarkRecipeV1 Recipe);
public sealed record PreparedBenchmark(int SchemaVersion, string Manifest, string ManifestSha256);
public sealed record PrepareProfileRecipeOptions(string RequestId, string OutputPath, ProfileRecipeV1 Recipe);
public sealed record PrepareTestMatrixOptions(string RequestId, string OutputPath, TestMatrixRecipeV1 Recipe);
public sealed record PrepareBuildMatrixOptions(string RequestId, string OutputPath, BuildMatrixRecipeV1 Recipe);
public sealed record PrepareBenchmarkMatrixOptions(string RequestId, string OutputPath, BenchmarkMatrixRecipeV1 Recipe);
public sealed record PreparedRecipe(int SchemaVersion, string Manifest, string ManifestSha256);
public sealed record AnalyzeProfileOptions(string RunDirectory);
public sealed record AnalyzedProfile(int SchemaVersion, string RunDirectory, string AnalysisFile);
public sealed record ReplayProfileOptions(string BaselineDirectory, string CandidateDirectory,
    string NodeId, string ResultsDirectory, string? CaseName = null, int HarnessIndex = 0,
    string Backend = "auto", double TimeoutSeconds = 300);
public sealed record ReplayProfileResult(int SchemaVersion, string RunDirectory, string AnalysisFile);
public sealed record PrepareTargetOptions(string RequestId, string ResultsDirectory, string Action,
    string BuildDirectory, string? Target = null, string? SecondTarget = null,
    string? Configuration = null, string[]? Arguments = null, string[]? SecondArguments = null,
    WorkflowToolchainBinding? Toolchain = null);
public sealed record MutationCampaignOptions(string SourceDirectory, string? ReportPath = null,
    string Suite = "all", string Compiler = "c++", string Seed = "496833221358",
    int Cases = 48, int Steps = 48, int TimeoutSeconds = 30);
public sealed record WeakMemoryCampaignOptions(string SourceDirectory, string RelacyDirectory,
    string? ReportPath = null, string Protocol = "all", string Compiler = "c++", int TimeoutSeconds = 60);
public sealed record FuzzSeedCampaignOptions(string Layer, string OutputDirectory);
public sealed record FuzzRunCampaignOptions(string BuildDirectory, string Layer, string ResultsDirectory,
    string? Backend = null, string? CorpusDirectory = null, string? Configuration = null,
    int DurationSeconds = 60, int InputTimeoutSeconds = 15, int MaxLength = 4096,
    bool ReplayOnly = false);
public sealed record FuzzTriageCampaignOptions(string RunDirectory);
public sealed record FuzzMergeCampaignOptions(string RunDirectory, string? OutputDirectory = null,
    bool Coverage = false);
public sealed record CampaignResult(int SchemaVersion, string Kind, string Status, int ExitCode,
    string? ArtifactDirectory);
public sealed record ImportHistoryOptions(string DatabasePath, string Directory, bool Recursive = false);
public sealed record ImportHistoryResult(int SchemaVersion, int Imported, int Unchanged, int Skipped);

[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IClientRpc
{
    [JsonRpcMethod("initialize")] Task<HostIdentity> InitializeAsync(InitRequest request, CancellationToken ct);
    [JsonRpcMethod("toolchains/discover")] Task<ToolchainInventory> ToolchainsDiscoverAsync(ToolchainDiscoverRequest request, CancellationToken ct);
    [JsonRpcMethod("toolchains/resolve")] Task<ToolchainResolution> ToolchainsResolveAsync(ToolchainResolveRequest request, CancellationToken ct);
    [JsonRpcMethod("toolchains/inspectEnvironment")] Task<DeveloperEnvironmentSnapshot> ToolchainsInspectEnvironmentAsync(ToolchainInspectEnvironmentRequest request, CancellationToken ct);

    [JsonRpcMethod("profile/prepare")] Task<PreparedWorkflow> ProfilePrepareAsync(PrepareProfileOptions request, CancellationToken ct);
    [JsonRpcMethod("benchmark/prepare")] Task<PreparedBenchmark> BenchmarkPrepareAsync(PrepareBenchmarkOptions request, CancellationToken ct);
    [JsonRpcMethod("profileRecipe/prepare")] Task<PreparedRecipe> ProfileRecipePrepareAsync(PrepareProfileRecipeOptions request, CancellationToken ct);
    [JsonRpcMethod("testMatrix/prepare")] Task<PreparedRecipe> TestMatrixPrepareAsync(PrepareTestMatrixOptions request, CancellationToken ct);
    [JsonRpcMethod("buildMatrix/prepare")] Task<PreparedRecipe> BuildMatrixPrepareAsync(PrepareBuildMatrixOptions request, CancellationToken ct);
    [JsonRpcMethod("benchmarkMatrix/prepare")] Task<PreparedRecipe> BenchmarkMatrixPrepareAsync(PrepareBenchmarkMatrixOptions request, CancellationToken ct);
    [JsonRpcMethod("profile/analyze")] Task<AnalyzedProfile> ProfileAnalyzeAsync(AnalyzeProfileOptions request, CancellationToken ct);
    [JsonRpcMethod("profile/replay")] Task<ReplayProfileResult> ProfileReplayAsync(ReplayProfileOptions request, CancellationToken ct);
    [JsonRpcMethod("target/prepare")] Task<PreparedWorkflow> TargetPrepareAsync(PrepareTargetOptions request, CancellationToken ct);
    [JsonRpcMethod("dagflow/mutation/run")] Task<CampaignResult> CampaignMutationAsync(MutationCampaignOptions request, CancellationToken ct);
    [JsonRpcMethod("dagflow/weakMemory/run")] Task<CampaignResult> CampaignWeakMemoryAsync(WeakMemoryCampaignOptions request, CancellationToken ct);
    [JsonRpcMethod("dagflow/fuzz/seed")] Task<CampaignResult> CampaignFuzzSeedAsync(FuzzSeedCampaignOptions request, CancellationToken ct);
    [JsonRpcMethod("dagflow/fuzz/run")] Task<CampaignResult> CampaignFuzzRunAsync(FuzzRunCampaignOptions request, CancellationToken ct);
    [JsonRpcMethod("dagflow/fuzz/triage")] Task<CampaignResult> CampaignFuzzTriageAsync(FuzzTriageCampaignOptions request, CancellationToken ct);
    [JsonRpcMethod("dagflow/fuzz/merge")] Task<CampaignResult> CampaignFuzzMergeAsync(FuzzMergeCampaignOptions request, CancellationToken ct);
    [JsonRpcMethod("history/import")] Task<ImportHistoryResult> HistoryImportAsync(ImportHistoryOptions request, CancellationToken ct);
    [JsonRpcMethod("workflow/start")] Task<OperationSnapshot> StartAsync(StartWorkflowOptions request, CancellationToken ct);
    [JsonRpcMethod("workflow/plan")] Task<JsonElement> PlanAsync(PlanRequest request, CancellationToken ct);
    [JsonRpcMethod("operations/get")] Task<OperationSnapshot> GetAsync(OpIdRequest request, CancellationToken ct);
    [JsonRpcMethod("operations/find")] Task<OperationSnapshot> FindAsync(FindRequest request, CancellationToken ct);
    [JsonRpcMethod("operations/cancel")] Task<CancellationReceipt> CancelAsync(CancelRequest request, CancellationToken ct);
    [JsonRpcMethod("operations/events")] Task<EventPage> EventsAsync(EventsRequest request, CancellationToken ct);
    [JsonRpcMethod("workflow/recoveryInspect")] Task<RecoverySnapshot> InspectAsync(OpIdRequest request, CancellationToken ct);
    [JsonRpcMethod("workflow/reconcile")] Task<RecoverySnapshot> ReconcileAsync(ReconcileRequest request, CancellationToken ct);
    [JsonRpcMethod("workflow/resume")] Task<RecoverySnapshot> AdmitResumeAsync(ResumeRequest request, CancellationToken ct);
    [JsonRpcMethod("workflow/resumeExecute")] Task<RecoverySnapshot> ExecuteResumeAsync(ResumeRequest request, CancellationToken ct);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ToolchainCandidate))]
[JsonSerializable(typeof(VisualStudioInstance))]
[JsonSerializable(typeof(ToolchainInventory))]
[JsonSerializable(typeof(ToolchainSelection))]
[JsonSerializable(typeof(ToolchainResolution))]
[JsonSerializable(typeof(DeveloperEnvironmentRequest))]
[JsonSerializable(typeof(DeveloperEnvironmentSnapshot))]
[JsonSerializable(typeof(ToolchainDiscoverRequest))]
[JsonSerializable(typeof(ToolchainResolveRequest))]
[JsonSerializable(typeof(ToolchainInspectEnvironmentRequest))]
[JsonSerializable(typeof(PreparedWorkflow))]
[JsonSerializable(typeof(WorkflowToolchainBinding))]
[JsonSerializable(typeof(ProfileVariantOptions))]
[JsonSerializable(typeof(List<ProfileVariantOptions>))]
[JsonSerializable(typeof(PrepareProfileOptions))]
[JsonSerializable(typeof(BenchmarkRecipeV1))]
[JsonSerializable(typeof(PrepareBenchmarkOptions))]
[JsonSerializable(typeof(PreparedBenchmark))]
[JsonSerializable(typeof(ProfileVariant))]
[JsonSerializable(typeof(ProfileRecipeV1))]
[JsonSerializable(typeof(TestMatrixGroupV1))]
[JsonSerializable(typeof(TestMatrixRecipeV1))]
[JsonSerializable(typeof(BuildVariantV1))]
[JsonSerializable(typeof(BuildMatrixRecipeV1))]
[JsonSerializable(typeof(BenchmarkAxisV1))]
[JsonSerializable(typeof(BenchmarkMatrixRecipeV1))]
[JsonSerializable(typeof(PrepareProfileRecipeOptions))]
[JsonSerializable(typeof(PrepareTestMatrixOptions))]
[JsonSerializable(typeof(PrepareBuildMatrixOptions))]
[JsonSerializable(typeof(PrepareBenchmarkMatrixOptions))]
[JsonSerializable(typeof(PreparedRecipe))]
[JsonSerializable(typeof(AnalyzeProfileOptions))]
[JsonSerializable(typeof(AnalyzedProfile))]
[JsonSerializable(typeof(ReplayProfileOptions))]
[JsonSerializable(typeof(ReplayProfileResult))]
[JsonSerializable(typeof(PrepareTargetOptions))]
[JsonSerializable(typeof(MutationCampaignOptions))]
[JsonSerializable(typeof(WeakMemoryCampaignOptions))]
[JsonSerializable(typeof(FuzzSeedCampaignOptions))]
[JsonSerializable(typeof(FuzzRunCampaignOptions))]
[JsonSerializable(typeof(FuzzTriageCampaignOptions))]
[JsonSerializable(typeof(FuzzMergeCampaignOptions))]
[JsonSerializable(typeof(CampaignResult))]
[JsonSerializable(typeof(ImportHistoryOptions))]
[JsonSerializable(typeof(ImportHistoryResult))]
[JsonSerializable(typeof(HostIdentity))]
[JsonSerializable(typeof(InitRequest))]
[JsonSerializable(typeof(StartWorkflowOptions))]
[JsonSerializable(typeof(OperationSnapshot))]
[JsonSerializable(typeof(CancellationReceipt))]
[JsonSerializable(typeof(RecoverySnapshot))]
[JsonSerializable(typeof(EventPage))]
[JsonSerializable(typeof(RpcErrorData))]
[JsonSerializable(typeof(StreamJsonRpc.Protocol.CommonErrorData))]
[JsonSerializable(typeof(OpIdRequest))]
[JsonSerializable(typeof(FindRequest))]
[JsonSerializable(typeof(CancelRequest))]
[JsonSerializable(typeof(EventsRequest))]
[JsonSerializable(typeof(ResumeRequest))]
[JsonSerializable(typeof(ReconcileRequest))]
[JsonSerializable(typeof(PlanRequest))]
[JsonSerializable(typeof(JsonElement))]
// StandardCancellationStrategy serializes its $/cancelRequest params using a
// boxed StreamJsonRpc.RequestId. Source-generated metadata is required in AOT;
// a pre-canceled invocation never exercises this path.
[JsonSerializable(typeof(StreamJsonRpc.RequestId))]
internal partial class ClientJson : JsonSerializerContext;
