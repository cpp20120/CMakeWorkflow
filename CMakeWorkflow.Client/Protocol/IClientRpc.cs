using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IClientRpc
{
    [JsonRpcMethod("initialize")]
    Task<HostIdentity> InitializeAsync(InitRequest request, CancellationToken ct);

    [JsonRpcMethod("toolchains/discover")]
    Task<ToolchainInventory> ToolchainsDiscoverAsync(ToolchainDiscoverRequest request, CancellationToken ct);

    [JsonRpcMethod("toolchains/resolve")]
    Task<ToolchainResolution> ToolchainsResolveAsync(ToolchainResolveRequest request, CancellationToken ct);

    [JsonRpcMethod("toolchains/inspectEnvironment")]
    Task<DeveloperEnvironmentSnapshot> ToolchainsInspectEnvironmentAsync(ToolchainInspectEnvironmentRequest request,
        CancellationToken ct);

    [JsonRpcMethod("profile/prepare")]
    Task<PreparedWorkflow> ProfilePrepareAsync(PrepareProfileOptions request, CancellationToken ct);

    [JsonRpcMethod("benchmark/prepare")]
    Task<PreparedBenchmark> BenchmarkPrepareAsync(PrepareBenchmarkOptions request, CancellationToken ct);

    [JsonRpcMethod("profileRecipe/prepare")]
    Task<PreparedRecipe> ProfileRecipePrepareAsync(PrepareProfileRecipeOptions request, CancellationToken ct);

    [JsonRpcMethod("testMatrix/prepare")]
    Task<PreparedRecipe> TestMatrixPrepareAsync(PrepareTestMatrixOptions request, CancellationToken ct);

    [JsonRpcMethod("buildMatrix/prepare")]
    Task<PreparedRecipe> BuildMatrixPrepareAsync(PrepareBuildMatrixOptions request, CancellationToken ct);

    [JsonRpcMethod("benchmarkMatrix/prepare")]
    Task<PreparedRecipe> BenchmarkMatrixPrepareAsync(PrepareBenchmarkMatrixOptions request, CancellationToken ct);

    [JsonRpcMethod("profile/analyze")]
    Task<AnalyzedProfile> ProfileAnalyzeAsync(AnalyzeProfileOptions request, CancellationToken ct);

    [JsonRpcMethod("profile/replay")]
    Task<ReplayProfileResult> ProfileReplayAsync(ReplayProfileOptions request, CancellationToken ct);

    [JsonRpcMethod("target/prepare")]
    Task<PreparedWorkflow> TargetPrepareAsync(PrepareTargetOptions request, CancellationToken ct);

    [JsonRpcMethod("dagflow/mutation/run")]
    Task<CampaignResult> CampaignMutationAsync(MutationCampaignOptions request, CancellationToken ct);

    [JsonRpcMethod("dagflow/weakMemory/run")]
    Task<CampaignResult> CampaignWeakMemoryAsync(WeakMemoryCampaignOptions request, CancellationToken ct);

    [JsonRpcMethod("dagflow/fuzz/seed")]
    Task<CampaignResult> CampaignFuzzSeedAsync(FuzzSeedCampaignOptions request, CancellationToken ct);

    [JsonRpcMethod("dagflow/fuzz/run")]
    Task<CampaignResult> CampaignFuzzRunAsync(FuzzRunCampaignOptions request, CancellationToken ct);

    [JsonRpcMethod("dagflow/fuzz/triage")]
    Task<CampaignResult> CampaignFuzzTriageAsync(FuzzTriageCampaignOptions request, CancellationToken ct);

    [JsonRpcMethod("dagflow/fuzz/merge")]
    Task<CampaignResult> CampaignFuzzMergeAsync(FuzzMergeCampaignOptions request, CancellationToken ct);

    [JsonRpcMethod("history/import")]
    Task<ImportHistoryResult> HistoryImportAsync(ImportHistoryOptions request, CancellationToken ct);

    [JsonRpcMethod("execution/report")]
    Task<JsonElement> ExecutionReportAsync(OpIdRequest request, CancellationToken ct);

    [JsonRpcMethod("execution/start")]
    Task<OperationSnapshot> ExecutionStartAsync(ExecutionStartOptions request, CancellationToken ct);

    [JsonRpcMethod("workflow/start")]
    Task<OperationSnapshot> StartAsync(StartWorkflowOptions request, CancellationToken ct);

    [JsonRpcMethod("workflow/plan")]
    Task<JsonElement> PlanAsync(PlanRequest request, CancellationToken ct);

    [JsonRpcMethod("operations/get")]
    Task<OperationSnapshot> GetAsync(OpIdRequest request, CancellationToken ct);

    [JsonRpcMethod("operations/report")]
    Task<OperationReportSnapshot> ReportAsync(OpIdRequest request, CancellationToken ct);

    [JsonRpcMethod("operations/artifacts/list")]
    Task<OperationArtifactList> ArtifactsAsync(OperationArtifactListRequest request, CancellationToken ct);

    [JsonRpcMethod("operations/artifacts/readChunk")]
    Task<OperationArtifactChunk> ReadArtifactAsync(OperationArtifactChunkRequest request, CancellationToken ct);

    [JsonRpcMethod("operations/find")]
    Task<OperationSnapshot> FindAsync(FindRequest request, CancellationToken ct);

    [JsonRpcMethod("operations/cancel")]
    Task<CancellationReceipt> CancelAsync(CancelRequest request, CancellationToken ct);

    [JsonRpcMethod("operations/events")]
    Task<EventPage> EventsAsync(EventsRequest request, CancellationToken ct);

    [JsonRpcMethod("workflow/recoveryInspect")]
    Task<RecoverySnapshot> InspectAsync(OpIdRequest request, CancellationToken ct);

    [JsonRpcMethod("workflow/reconcile")]
    Task<RecoverySnapshot> ReconcileAsync(ReconcileRequest request, CancellationToken ct);

    [JsonRpcMethod("workflow/resume")]
    Task<RecoverySnapshot> AdmitResumeAsync(ResumeRequest request, CancellationToken ct);

    [JsonRpcMethod("workflow/resumeExecute")]
    Task<RecoverySnapshot> ExecuteResumeAsync(ResumeRequest request, CancellationToken ct);
}
