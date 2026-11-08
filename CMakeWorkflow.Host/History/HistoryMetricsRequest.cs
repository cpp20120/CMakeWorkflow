namespace CMakeWorkflow.Host.History;

public sealed record HistoryMetricsRequest(
    string DatabasePath,
    string? RunId = null,
    string? Metric = null,
    string? Variant = null,
    string? Scenario = null);
