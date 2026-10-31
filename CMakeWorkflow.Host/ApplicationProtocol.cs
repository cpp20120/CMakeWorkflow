using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record ContextInspectRequest(string BuildDirectory);
public sealed record TargetResolveRequest(string BuildDirectory, string Target, string? Configuration = null);
public sealed record WorkflowPlanRequest(string Manifest, string[]? Only = null, string? ControllerBuildDirectory = null);
public sealed record RunGetRequest(string RunDirectory);
public sealed record HistoryRunsRequest(string DatabasePath, int Limit = 50, string? Status = null,
    string? Since = null, string? Until = null, string? Workflow = null, string? Variant = null);
public sealed record HistoryShowRequest(string DatabasePath, string RunId);
public sealed record HistoryMetricsRequest(string DatabasePath, string? RunId = null, string? Metric = null,
    string? Variant = null, string? Scenario = null);
public sealed record HistoryCompareRequest(string DatabasePath, string BaselineRunId, string CandidateRunId);
public sealed record HistoryDetectRequest(string DatabasePath, string BaselineRunId, string CandidateRunId,
    string Metric, string Direction, double MinimumEffectPercent, string? Variant = null,
    string? Scenario = null, double Alpha = 0.05);
public sealed record WorkflowStartRequest(string ClientRequestId, string Manifest, string ManifestSha256,
    int Jobs, string ResultsDirectory);
public sealed record OperationCancelRequest(string OperationId, long ExpectedRevision);
public sealed record OperationCancelResult(string OperationId, string State, long Revision, bool Requested);
public sealed record WorkflowResumeRequest(string OperationId, long ExpectedRevision, bool ConfirmedQuiescent);
public sealed record OperationEventsRequest(string OperationId, long AfterSequence = 0, int Limit = 100);
public sealed record OperationEventsPage(string OperationId, long AfterSequence, long NextSequence,
    bool HasMore, List<WorkflowEvent> Events);
public sealed record RecoveryInspectRequest(string OperationId);
public sealed record RecoveryReconcileRequest(string OperationId, long ExpectedRevision, string Decision, string Note, bool ConfirmedQuiescent);
public sealed record RecoveryInspectionView(string OperationId, string AcceptanceState, string? TerminalState,
    bool CancellationRequested, string? RunDirectory, string? CheckpointSha256,
    string? Decision, long Revision, string? Note);
public sealed record OperationFindRequest(string ClientRequestId);
public sealed record OperationGetRequest(string OperationId);
public sealed record OperationReference(string WorkspaceId, string OperationId, string ClientRequestId,
    string Kind, string State, string ManifestSha256, string ManifestBaseDirectory);
public sealed record ApplicationError(string Code, string Message);

public partial interface IHostRpc
{
    [JsonRpcMethod("workflow/start")]
    Task<OperationReference> WorkflowStartAsync(WorkflowStartRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("operations/cancel")]
    Task<OperationCancelResult> OperationCancelAsync(OperationCancelRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("workflow/resume")]
    Task<RecoveryInspectionView> WorkflowResumeAsync(WorkflowResumeRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("workflow/resumeExecute")]
    Task<RecoveryInspectionView> WorkflowResumeExecuteAsync(WorkflowResumeRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("operations/events")]
    Task<OperationEventsPage> OperationEventsAsync(OperationEventsRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("workflow/recoveryInspect")]
    Task<RecoveryInspectionView> RecoveryInspectAsync(RecoveryInspectRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("workflow/reconcile")]
    Task<RecoveryInspectionView> RecoveryReconcileAsync(RecoveryReconcileRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("operations/find")]
    Task<OperationReference> OperationFindAsync(OperationFindRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("operations/get")]
    Task<OperationReference> OperationGetAsync(OperationGetRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("context/inspect")]
    Task<ObservedBuildContext> ContextInspectAsync(ContextInspectRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("target/resolve")]
    Task<TargetResolutionView> TargetResolveAsync(TargetResolveRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("workflow/plan")]
    Task<WorkflowPlanView> WorkflowPlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("runs/get")]
    Task<WorkflowReportSnapshot> RunGetAsync(RunGetRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("history/runs")]
    Task<List<HistoryRun>> HistoryRunsAsync(HistoryRunsRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("history/show")]
    Task<HistoryDetail> HistoryShowAsync(HistoryShowRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("history/metrics")]
    Task<List<HistoryMeasurement>> HistoryMetricsAsync(HistoryMetricsRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("history/query")]
    Task<List<HistoryMeasurement>> HistoryQueryAsync(HistoryMetricsRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("history/compare")]
    Task<List<HistoryDifference>> HistoryCompareAsync(HistoryCompareRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("history/detect")]
    Task<HistoryDetectionReport> HistoryDetectAsync(HistoryDetectRequest request, CancellationToken cancellationToken);
}

