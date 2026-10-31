using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;

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
internal sealed record InitRequest(int ProtocolVersion, string WorkspaceDirectory);
internal sealed record OpIdRequest(string OperationId);
internal sealed record FindRequest(string ClientRequestId);
internal sealed record CancelRequest(string OperationId, long ExpectedRevision);
internal sealed record EventsRequest(string OperationId, long AfterSequence, int Limit);
internal sealed record ResumeRequest(string OperationId, long ExpectedRevision, bool ConfirmedQuiescent);
internal sealed record ReconcileRequest(string OperationId, long ExpectedRevision, string Decision, string Note, bool ConfirmedQuiescent);
internal sealed record PlanRequest(string Manifest, string[]? Only = null, string? ControllerBuildDirectory = null);

[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IClientRpc
{
    [JsonRpcMethod("initialize")] Task<HostIdentity> InitializeAsync(InitRequest request, CancellationToken ct);
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
internal partial class ClientJson : JsonSerializerContext;
