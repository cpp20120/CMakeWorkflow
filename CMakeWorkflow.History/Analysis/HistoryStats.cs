// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History.Analysis;
#pragma warning restore IDE0130

public sealed record HistoryStats(
    int Count,
    double Median,
    double Min,
    double Max,
    double Mean,
    double StandardDeviation);
