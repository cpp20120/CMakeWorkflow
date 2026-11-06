namespace CMakeWorkflow.Pipelines.Persistence;

/// <summary>Persisted pipeline stage statuses; a running intent is never evidence of external dispatch.</summary>
public static class PipelineStageStates
{
    public const string Pending = "pending";
    public const string DispatchIntent = "dispatch_intent";
    public const string WaitingExternal = "waiting_external";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string OutcomeUnknown = "outcome_unknown";
}

/// <summary>Read-only stage projection; binding values are identities, not success evidence.</summary>
public sealed record PipelineStageSnapshot(
    string Id,
    string Kind,
    string Status,
    long Revision,
    string? ChildRequestId,
    string? ChildOperationId,
    string? ExternalReference,
    string? EvidenceReference);

/// <summary>Detached operation projection. ManifestJson is the original immutable admitted snapshot.</summary>
public sealed record PipelineRunSnapshot(
    string OperationId,
    string ClientRequestId,
    string Name,
    string ManifestSha256,
    string ManifestJson,
    long Revision,
    string Status,
    IReadOnlyList<PipelineStageSnapshot> Stages);

/// <summary>One append-only transition, identified by the operation-wide revision.</summary>
public sealed record PipelineLedgerEvent(
    long Revision,
    string? StageId,
    string Kind,
    string RecordedAt,
    string? Detail);

/// <summary>Host-scoped, exact child admission parameters persisted atomically before dispatch.</summary>
/// <remarks>Only Host validates filesystem authorization; this value provides lexical identity and recovery evidence.</remarks>
public sealed record PipelineChildLaunch(
    string WorkspaceDirectory,
    string Manifest,
    string ManifestSha256,
    int Jobs,
    string ResultsDirectory);
