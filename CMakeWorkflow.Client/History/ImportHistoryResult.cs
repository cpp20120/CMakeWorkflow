namespace CMakeWorkflow.Client.History;

public sealed record ImportHistoryResult(int SchemaVersion, int Imported, int Unchanged, int Skipped);
