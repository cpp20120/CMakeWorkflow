namespace CMakeWorkflow.History.Models;

public sealed record HistoryImportResult(int SchemaVersion, int Imported, int Unchanged, int Skipped);
