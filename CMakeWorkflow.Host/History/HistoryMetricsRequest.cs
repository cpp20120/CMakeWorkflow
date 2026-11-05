#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

public sealed record HistoryMetricsRequest(
    string DatabasePath,
    string? RunId = null,
    string? Metric = null,
    string? Variant = null,
    string? Scenario = null);
