
namespace CMakeWorkflow.Host.Workflows;

/// <summary>A durable read projection, independent of CMake and process report schemas.</summary>
/// <remarks>Node information exists only after the Host verifies finalization and report bytes.
/// During execution the only authoritative read state is the operation admission/cancellation state;
/// use operations/events for live progress.</remarks>

public sealed record OperationOverview(
    int SchemaVersion, OperationReference Operation, bool IsFinalized,
    string? RunDirectory, string? ReportSha256,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error,
    string? HostId, string? EnvironmentFingerprint,
List<OperationNodeView> Nodes);

public sealed record OperationNodeView(
    string Id, string Kind, string Status,
List<string> Dependencies,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, string? Error, string? Stdout, string? Stderr);
