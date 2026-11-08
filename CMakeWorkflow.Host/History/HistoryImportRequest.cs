namespace CMakeWorkflow.Host.History;

public sealed record HistoryImportRequest(string DatabasePath, string Directory, bool Recursive = false);
