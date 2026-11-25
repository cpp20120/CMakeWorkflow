namespace CMakeWorkflow.Contracts.History;

/// <summary>Selects two series explicitly. Distinct recorded build environments can be compared
/// descriptively when both expected identities are supplied and intentionally allowed.</summary>
public sealed record HistoryComparisonSelection(string Metric)
{
    public string? BaselineVariant { get; init; }
    public string? CandidateVariant { get; init; }
    public string? Scenario { get; init; }
    public string? BaselineEnvironment { get; init; }
    public string? CandidateEnvironment { get; init; }
    public bool AllowDifferentBuildEnvironments { get; init; }
}
