// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.History;
#pragma warning restore IDE0130

public sealed record HistoryImportRequest(string DatabasePath, string Directory, bool Recursive = false);
