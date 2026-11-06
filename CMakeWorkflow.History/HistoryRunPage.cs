using CMakeWorkflow.History.Models;

namespace CMakeWorkflow.History;

/// <summary>Workspace-authorized, keyset-paginated run history.</summary>

public sealed record HistoryRunPage(int SchemaVersion,
List<HistoryRun> Items, string? NextCursor);
