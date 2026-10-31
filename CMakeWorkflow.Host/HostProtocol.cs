using System.Text.Json.Serialization;
using CMakeWorkflow.Tooling;
using CMakeWorkflow.History;
using PolyType;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

// Protocol v1 is capability-negotiated. DTOs are shared by the CLI client and
// server; TypeScript clients use positional JSON-RPC arguments: [request].
public sealed record InitializeRequest(int ProtocolVersion, string WorkspaceDirectory);
public sealed record HostInfo(int ProtocolVersion, int ProcessId, string WorkspaceDirectory,
    string[] Capabilities, List<ToolingProfileView> Profiles);
public sealed record DoctorRequest(string OperationId, string[] Profiles, string? InstallDirectory);
public sealed record DoctorProgress(string OperationId, int Sequence, string Stage, ToolStatus? Tool);
public sealed record PlanFixRequest(string[] Profiles, string? InstallDirectory,
    PackageManagerKind PackageManager = PackageManagerKind.Auto, bool AllowFallback = true,
    string? RunDirectory = null);
public sealed record FixPlan(int SchemaVersion, string PlanId, DateTimeOffset ExpiresAt, ToolingPlan Plan,
    string? RunDirectory = null, string? RunSnapshot = null);
public sealed record ApplyFixRequest(string OperationId, string PlanId);
public sealed record FixProgress(string OperationId, int Sequence, string Stage, string? Tool, string? Message);
public sealed record EnvironmentPlanRequest(string RequirementsPath);
public sealed record EnvironmentApplyRequest(string RequirementsPath, string ApprovalSha256, bool AllowSystemChanges);
public sealed record FixResult(int SchemaVersion, string Status, ToolingResult? Installation,
    ToolingPlan? Verification, string[] Diagnostics, bool ReadyToResume = false);

[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IHostRpc
{
    [JsonRpcMethod("initialize")]
    Task<HostInfo> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("tooling/doctor")]
    Task<ToolingPlan> DoctorAsync(DoctorRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("tooling/planFix")]
    Task<FixPlan> PlanFixAsync(PlanFixRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("environment/plan")]
    Task<HostPreparationPlan> EnvironmentPlanAsync(EnvironmentPlanRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("environment/apply")]
    Task<HostPreparationResult> EnvironmentApplyAsync(EnvironmentApplyRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod("tooling/applyFix")]
    Task<FixResult> ApplyFixAsync(ApplyFixRequest request, CancellationToken cancellationToken);
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IHostNotifications
{
    [JsonRpcMethod("tooling/progress")]
    void Progress(DoctorProgress progress);
    [JsonRpcMethod("tooling/fixProgress")]
    void FixProgress(FixProgress progress);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = [typeof(JsonStringEnumConverter<ToolPlatform>),
        typeof(JsonStringEnumConverter<PackageManagerKind>)])]
[JsonSerializable(typeof(InitializeRequest))]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(DoctorRequest))]
[JsonSerializable(typeof(DoctorProgress))]
[JsonSerializable(typeof(ToolingPlan))]
[JsonSerializable(typeof(PlanFixRequest))]
[JsonSerializable(typeof(FixPlan))]
[JsonSerializable(typeof(ApplyFixRequest))]
[JsonSerializable(typeof(FixProgress))]
[JsonSerializable(typeof(FixResult))]
[JsonSerializable(typeof(EnvironmentPlanRequest))]
[JsonSerializable(typeof(EnvironmentApplyRequest))]
[JsonSerializable(typeof(HostPreparationPlan))]
[JsonSerializable(typeof(HostPreparationResult))]
[JsonSerializable(typeof(ContextInspectRequest))]
[JsonSerializable(typeof(TargetResolveRequest))]
[JsonSerializable(typeof(WorkflowPlanRequest))]
[JsonSerializable(typeof(RunGetRequest))]
[JsonSerializable(typeof(HistoryRunsRequest))]
[JsonSerializable(typeof(HistoryShowRequest))]
[JsonSerializable(typeof(HistoryMetricsRequest))]
[JsonSerializable(typeof(HistoryCompareRequest))]
[JsonSerializable(typeof(HistoryDetectRequest))]
[JsonSerializable(typeof(ObservedBuildContext))]
[JsonSerializable(typeof(TargetResolutionView))]
[JsonSerializable(typeof(WorkflowPlanView))]
[JsonSerializable(typeof(WorkflowReportSnapshot))]
[JsonSerializable(typeof(List<HistoryRun>))]
[JsonSerializable(typeof(HistoryDetail))]
[JsonSerializable(typeof(List<HistoryMeasurement>))]
[JsonSerializable(typeof(List<HistoryDifference>))]
[JsonSerializable(typeof(HistoryDetectionReport))]
[JsonSerializable(typeof(WorkflowStartRequest))]
[JsonSerializable(typeof(OperationCancelRequest))]
[JsonSerializable(typeof(OperationCancelResult))]
[JsonSerializable(typeof(WorkflowResumeRequest))]
[JsonSerializable(typeof(OperationEventsRequest))]
[JsonSerializable(typeof(OperationEventsPage))]
[JsonSerializable(typeof(List<WorkflowEvent>))]
[JsonSerializable(typeof(WorkflowEvent))]
[JsonSerializable(typeof(RecoveryInspectRequest))]
[JsonSerializable(typeof(RecoveryReconcileRequest))]
[JsonSerializable(typeof(RecoveryInspectionView))]
[JsonSerializable(typeof(OperationFindRequest))]
[JsonSerializable(typeof(OperationGetRequest))]
[JsonSerializable(typeof(OperationReference))]
[JsonSerializable(typeof(ApplicationError))]
[JsonSerializable(typeof(StreamJsonRpc.Protocol.CommonErrorData))]
public partial class HostJson : JsonSerializerContext;
