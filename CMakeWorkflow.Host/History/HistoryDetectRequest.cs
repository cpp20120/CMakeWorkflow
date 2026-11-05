#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

public sealed record HistoryDetectRequest(
    string DatabasePath,
    string BaselineRunId,
    string CandidateRunId,
    string Metric,
    string Direction,
    double MinimumEffectPercent,
    string? Variant = null,
    string? Scenario = null,
    double Alpha = 0.05);
