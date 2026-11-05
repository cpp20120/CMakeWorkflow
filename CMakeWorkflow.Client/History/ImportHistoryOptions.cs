namespace CMakeWorkflow.Client.History;

public sealed record ImportHistoryOptions(string DatabasePath, string Directory, bool Recursive = false);
