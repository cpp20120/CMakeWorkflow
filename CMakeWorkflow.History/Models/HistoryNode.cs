#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History;
#pragma warning restore IDE0130

public sealed record HistoryNode(
    string RunId,
    string Id,
    string Kind,
    string Status,
    double? DurationSeconds,
    string? Dependencies,
    string? BuildDirectory,
    string? Configuration,
    string? Target);
