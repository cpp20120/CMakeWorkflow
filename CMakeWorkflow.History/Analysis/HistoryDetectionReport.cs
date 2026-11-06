
// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History.Analysis;
#pragma warning restore IDE0130

public sealed record HistoryDetectionReport(
    int SchemaVersion,
    string BaselineRun,
    string CandidateRun,
    string Metric,
    string Direction,
    double MinimumEffectPercent,
    double Alpha,
    string Test,

List<HistoryDetection> Results);
