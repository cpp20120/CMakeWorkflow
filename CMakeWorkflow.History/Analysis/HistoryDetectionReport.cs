using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History;
#pragma warning restore IDE0130

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record HistoryDetectionReport(
    int SchemaVersion,
    string BaselineRun,
    string CandidateRun,
    string Metric,
    string Direction,
    double MinimumEffectPercent,
    double Alpha,
    string Test,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<HistoryDetection> Results);
