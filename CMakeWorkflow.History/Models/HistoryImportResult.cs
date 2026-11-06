// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History.Models;
#pragma warning restore IDE0130

public sealed record HistoryImportResult(int SchemaVersion, int Imported, int Unchanged, int Skipped);
