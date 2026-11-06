// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.History;
#pragma warning restore IDE0130

public sealed record HistoryMetricsRequest(
    string DatabasePath,
    string? RunId = null,
    string? Metric = null,
    string? Variant = null,
    string? Scenario = null);
