// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History.Models;
#pragma warning restore IDE0130

public sealed record HistoryArtifact(
    string RunId,
    string? NodeId,
    string Type,
    string Path,
    string? Sha256,
    string? Metadata,
    bool Available = true);
