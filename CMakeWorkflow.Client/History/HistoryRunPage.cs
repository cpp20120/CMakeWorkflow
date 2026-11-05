namespace CMakeWorkflow.Client;

public sealed record HistoryRunPage(int SchemaVersion, List<HistoryRun> Items, string? NextCursor);
