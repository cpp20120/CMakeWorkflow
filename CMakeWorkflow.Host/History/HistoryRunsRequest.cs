namespace CMakeWorkflow.Host.History;

public sealed record HistoryRunsRequest(
    string DatabasePath,
    int Limit = 50,
    string? Status = null,
    string? Since = null,
    string? Until = null,
    string? Workflow = null,
    string? Variant = null);
