#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

public sealed record ImportHistoryResult(int SchemaVersion, int Imported, int Unchanged, int Skipped);
