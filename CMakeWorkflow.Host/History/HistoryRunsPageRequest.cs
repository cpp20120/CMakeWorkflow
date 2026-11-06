// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.History;
#pragma warning restore IDE0130

public sealed record HistoryRunsPageRequest(string DatabasePath, int Limit = 50,
    string? Status = null, string? Since = null, string? Until = null,
    string? Workflow = null, string? Variant = null, string? Cursor = null);
