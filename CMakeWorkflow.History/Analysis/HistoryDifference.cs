#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History;
#pragma warning restore IDE0130

public sealed record HistoryDifference(
    string Metric,
    string Unit,
    string? Variant,
    string? Scenario,
    string Method,
    string Status,
    string? Reason,
    HistoryStats? Baseline,
    HistoryStats? Candidate,
    double? AbsoluteChange,
    double? PercentChange);
