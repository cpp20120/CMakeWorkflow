#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History;
#pragma warning restore IDE0130

public sealed record HistoryMeasurement(
    string RunId,
    string? NodeId,
    string Metric,
    string Unit,
    double Value,
    string? Variant,
    string? Scenario,
    string Source,
    string Method,
    int SampleIndex,
    string? Workload,
    string? Environment);
