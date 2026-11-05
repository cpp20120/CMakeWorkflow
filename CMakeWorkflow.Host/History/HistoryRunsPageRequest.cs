namespace CMakeWorkflow.Host;

public sealed record HistoryRunsPageRequest(string DatabasePath, int Limit = 50,
    string? Status = null, string? Since = null, string? Until = null,
    string? Workflow = null, string? Variant = null, string? Cursor = null);
