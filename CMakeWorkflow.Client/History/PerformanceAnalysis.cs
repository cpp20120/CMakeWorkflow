using CMakeWorkflow.Contracts.History;

namespace CMakeWorkflow.Client.History;

/// <summary>Dimensions that must match before samples are summarized together.</summary>
public sealed record MeasurementSeries(string RunId, string Metric, string Unit, string? Variant, string? Scenario,
    string Method, string? Workload, string? Environment, string? Direction);

/// <summary>A descriptive summary. Measurements retain the original identity and provenance.</summary>
public sealed record MeasurementSummary(MeasurementSeries Series, HistorySampleSummary Statistics,
    IReadOnlyList<HistoryMeasurement> Measurements);

public static class PerformanceAnalysis
{
    /// <summary>Groups a bounded measurement result without pooling runs or unlike measurement conditions.</summary>
    public static IReadOnlyList<MeasurementSummary> Summarize(this IEnumerable<HistoryMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        return Array.AsReadOnly(measurements.GroupBy(row => new MeasurementSeries(row.RunId, row.Metric, row.Unit,
            row.Variant, row.Scenario, row.Method, row.Workload, row.Environment, row.Direction))
            .OrderBy(group => group.Key.RunId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Metric, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Unit, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Variant, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Scenario, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Method, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Workload, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Environment, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Direction, StringComparer.Ordinal)
            .Select(group =>
            {
                HistoryMeasurement[] samples = [.. group];
                return new MeasurementSummary(group.Key, HistorySampleSummary.Calculate(samples.Select(row => row.Value))!,
                    Array.AsReadOnly(samples));
            }).ToArray());
    }
}

public enum MetricDirection { LowerIsBetter, HigherIsBetter }
public enum RegressionThresholdUnit { RelativePercent, Absolute }
public enum RegressionOutcome { Passed, Failed, Inconclusive }

/// <summary>A positive deterioration limit. Relative thresholds use percentage points (5 means five percent).</summary>
public sealed record RegressionPolicy(MetricDirection Direction, double MaximumRegression,
    RegressionThresholdUnit Unit = RegressionThresholdUnit.RelativePercent, int MinimumSamples = 5)
{
    public bool AllowIntentionalBuildDifferences { get; init; }
    public bool RequireStatisticalEvidenceToFail { get; init; }
}

public sealed record RegressionDecision(RegressionOutcome Outcome, string Code, string Message,
    double? ObservedRegression = null);

public static class RegressionGate
{
    /// <summary>Evaluates median deterioration against an inclusive tolerance.</summary>
    /// <remarks>Optional detection must come from the same explicit run pair. A statistical policy can make an
    /// above-tolerance observation inconclusive when existing History detection has not confirmed it.</remarks>
    public static RegressionDecision Evaluate(HistoryDifference? comparison, RegressionPolicy policy,
        HistoryDetectionReport? detection = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(policy.Direction) || !Enum.IsDefined(policy.Unit) ||
            !double.IsFinite(policy.MaximumRegression) || policy.MaximumRegression < 0 || policy.MinimumSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "A finite nonnegative threshold and positive sample count are required.");
        }

        if (comparison is null || comparison.Baseline is not { } baseline || comparison.Candidate is not { } candidate)
        {
            return new(RegressionOutcome.Inconclusive, "missing_samples", "Both sides must have measurements.");
        }
        if (comparison.Status != "compatible" &&
            !(comparison.Status == "intentional" && policy.AllowIntentionalBuildDifferences))
        {
            return new(RegressionOutcome.Inconclusive, "comparability", comparison.Reason ?? comparison.Status);
        }
        if (comparison.RunsSucceeded != true)
        {
            return new(RegressionOutcome.Inconclusive, "run_outcome", "Both runs must have recorded successful execution.");
        }
        if (baseline.Count < policy.MinimumSamples || candidate.Count < policy.MinimumSamples)
        {
            return new(RegressionOutcome.Inconclusive, "insufficient_samples", "There are too few samples for this policy.");
        }
        if (policy.Unit == RegressionThresholdUnit.RelativePercent && baseline.Median <= 0)
        {
            return new(RegressionOutcome.Inconclusive, "nonpositive_baseline", "Percentage thresholds require a positive baseline median.");
        }

        double degradation = policy.Direction == MetricDirection.LowerIsBetter
            ? candidate.Median - baseline.Median : baseline.Median - candidate.Median;
        if (policy.Unit == RegressionThresholdUnit.RelativePercent)
        {
            degradation = degradation / baseline.Median * 100;
        }
        return degradation switch
        {
            _ when !double.IsFinite(degradation) =>
                new(RegressionOutcome.Inconclusive, "nonfinite_effect", "The measured effect is not finite."),
            _ when degradation <= policy.MaximumRegression =>
                new(RegressionOutcome.Passed, "within_tolerance", "Median deterioration is within tolerance.", degradation),
            _ when policy.RequireStatisticalEvidenceToFail && !Confirmed(comparison, policy.Direction, detection) =>
                new(RegressionOutcome.Inconclusive, "unconfirmed_regression",
                    "Median deterioration exceeds tolerance without matching History detection evidence.", degradation),
            _ => new(RegressionOutcome.Failed, "above_tolerance", "Median deterioration exceeds tolerance.", degradation)
        };
    }

    /// <summary>Combines gates with failure taking precedence, then inconclusive. An empty set is inconclusive.</summary>
    public static RegressionOutcome Aggregate(IEnumerable<RegressionDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        RegressionOutcome outcome = RegressionOutcome.Inconclusive;
        bool inconclusive = false;
        foreach (RegressionDecision decision in decisions)
        {
            if (decision.Outcome == RegressionOutcome.Failed)
            {
                return RegressionOutcome.Failed;
            }
            inconclusive |= decision.Outcome != RegressionOutcome.Passed;
            outcome = RegressionOutcome.Passed;
        }
        return inconclusive ? RegressionOutcome.Inconclusive : outcome;
    }

    private static bool Confirmed(HistoryDifference comparison, MetricDirection direction, HistoryDetectionReport? detection) =>
        comparison.Status == "compatible" && detection is not null &&
        comparison.BaselineRunId == detection.BaselineRun && comparison.CandidateRunId == detection.CandidateRun &&
        comparison.BaselineVariant == comparison.CandidateVariant &&
        detection.Direction == (direction == MetricDirection.LowerIsBetter ? "higher-worse" : "lower-worse") &&
        detection.Results.Count(row => row.Metric == comparison.Metric && row.Variant == comparison.Variant &&
            row.Scenario == comparison.Scenario && row.Unit == comparison.Unit && row.Method == comparison.Method &&
            row.Baseline == comparison.Baseline && row.Candidate == comparison.Candidate &&
            row.Status == "regression" && row.AdjustedPValue <= detection.Alpha) == 1;
}
