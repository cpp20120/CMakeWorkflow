using System.Diagnostics.CodeAnalysis;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

/// <summary>A durable read projection, independent of CMake and process report schemas.</summary>
/// <remarks>Node information exists only after the Host verifies finalization and report bytes.
/// During execution the only authoritative read state is the operation admission/cancellation state;
/// use operations/events for live progress.</remarks>
[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record OperationOverview(
    int SchemaVersion, OperationReference Operation, bool IsFinalized,
    string? RunDirectory, string? ReportSha256,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error,
    string? HostId, string? EnvironmentFingerprint, [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<OperationNodeView> Nodes);

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record OperationNodeView(
    string Id, string Kind, string Status, [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<string> Dependencies,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, string? Error, string? Stdout, string? Stderr);
