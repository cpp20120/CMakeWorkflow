using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public partial interface IHostRpc
{
    [JsonRpcMethod("workflow/start")]
    Task<OperationReference> WorkflowStartAsync(WorkflowStartRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("operations/cancel")]
    Task<OperationCancelResult> OperationCancelAsync(OperationCancelRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("workflow/resume")]
    Task<RecoveryInspectionView>
        WorkflowResumeAsync(WorkflowResumeRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("workflow/resumeExecute")]
    Task<RecoveryInspectionView> WorkflowResumeExecuteAsync(WorkflowResumeRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("operations/events")]
    Task<OperationEventsPage> OperationEventsAsync(OperationEventsRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("workflow/recoveryInspect")]
    Task<RecoveryInspectionView> RecoveryInspectAsync(RecoveryInspectRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("workflow/reconcile")]
    Task<RecoveryInspectionView> RecoveryReconcileAsync(RecoveryReconcileRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("operations/find")]
    Task<OperationReference> OperationFindAsync(OperationFindRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("operations/get")]
    Task<OperationReference> OperationGetAsync(OperationGetRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("operations/report")]
    Task<WorkflowReportSnapshot> OperationReportAsync(OperationGetRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("operations/artifacts/list")]
    Task<OperationArtifactList> OperationArtifactListAsync(OperationArtifactListRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("operations/artifacts/readChunk")]
    Task<OperationArtifactChunk> OperationArtifactReadAsync(OperationArtifactChunkRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("context/inspect")]
    Task<ObservedBuildContext> ContextInspectAsync(ContextInspectRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("target/resolve")]
    Task<TargetResolutionView> TargetResolveAsync(TargetResolveRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("workflow/plan")]
    Task<WorkflowPlanView> WorkflowPlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("profile/prepare")]
    Task<ProfilePreparation> ProfilePrepareAsync(ProfilePrepareRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("benchmark/prepare")]
    Task<BenchmarkPreparation> BenchmarkPrepareAsync(BenchmarkPrepareRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("profileRecipe/prepare")]
    Task<ProfileRecipePreparation> ProfileRecipePrepareAsync(ProfileRecipePrepareRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("testMatrix/prepare")]
    Task<TestMatrixPreparation> TestMatrixPrepareAsync(TestMatrixPrepareRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("buildMatrix/prepare")]
    Task<BuildMatrixPreparation> BuildMatrixPrepareAsync(BuildMatrixPrepareRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("benchmarkMatrix/prepare")]
    Task<BenchmarkMatrixPreparation> BenchmarkMatrixPrepareAsync(BenchmarkMatrixPrepareRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("profile/analyze")]
    Task<ProfileAnalysis> ProfileAnalyzeAsync(ProfileAnalyzeRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("target/prepare")]
    Task<TargetPreparation> TargetPrepareAsync(TargetPrepareRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("profile/replay")]
    Task<ProfileReplayResponse> ProfileReplayAsync(ProfileReplayRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("dagflow/mutation/run")]
    Task<CampaignExecutionResult> CampaignMutationAsync(CampaignMutationRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("dagflow/weakMemory/run")]
    Task<CampaignExecutionResult> CampaignWeakMemoryAsync(CampaignWeakMemoryRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("dagflow/fuzz/seed")]
    Task<CampaignExecutionResult> CampaignFuzzSeedAsync(CampaignFuzzSeedRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("dagflow/fuzz/run")]
    Task<CampaignExecutionResult> CampaignFuzzRunAsync(CampaignFuzzRunRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("dagflow/fuzz/triage")]
    Task<CampaignExecutionResult> CampaignFuzzTriageAsync(CampaignFuzzTriageRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("dagflow/fuzz/merge")]
    Task<CampaignExecutionResult> CampaignFuzzMergeAsync(CampaignFuzzMergeRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("runs/get")]
    Task<WorkflowReportSnapshot> RunGetAsync(RunGetRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("history/import")]
    Task<HistoryImportResult> HistoryImportAsync(HistoryImportRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("history/runs")]
    Task<List<HistoryRun>> HistoryRunsAsync(HistoryRunsRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("history/show")]
    Task<HistoryDetail> HistoryShowAsync(HistoryShowRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("history/metrics")]
    Task<List<HistoryMeasurement>> HistoryMetricsAsync(HistoryMetricsRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("history/query")]
    Task<List<HistoryMeasurement>>
        HistoryQueryAsync(HistoryMetricsRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("history/compare")]
    Task<List<HistoryDifference>> HistoryCompareAsync(HistoryCompareRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("history/detect")]
    Task<HistoryDetectionReport> HistoryDetectAsync(HistoryDetectRequest request, CancellationToken cancellationToken);
}