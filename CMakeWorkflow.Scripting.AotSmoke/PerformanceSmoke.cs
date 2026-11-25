using System.Diagnostics;
using CMakeWorkflow.Client.History;
using CMakeWorkflow.Contracts.History;

internal static class PerformanceSmoke
{
    internal static void Run()
    {
        HistoryMeasurement sample = new("run", "trial", "latency", "ns", 2, "release", null,
            "report", "wall", 0, "input", "env1");
        IReadOnlyList<MeasurementSummary> groups = new[]
        {
            sample, sample with { Value = 4, SampleIndex = 1 }, sample with { Unit = "ms" },
            sample with { Environment = "env2" }, sample with { Scenario = "other" }
        }.Summarize();
        Check(groups.Count == 4 && groups.Single(group => group.Statistics.Count == 2).Statistics
            == new HistorySampleSummary(2, 3, 2, 4, 3, 1), "descriptive statistics and provenance grouping");
        Check(HistorySampleSummary.Calculate([]) is null &&
            HistorySampleSummary.Calculate([3])?.StandardDeviation == 0, "empty and singleton summaries");
        try
        {
            _ = HistorySampleSummary.Calculate([double.NaN]);
            throw new UnreachableException("Accepted a non-finite sample.");
        }
        catch (ArgumentException) { }

        HistoryStats baseline = new(6, 100, 100, 100, 100, 0);
        HistoryStats candidate = new(6, 105, 105, 105, 105, 0);
        HistoryDifference row = new("latency", "ns", "release", null, "wall", "compatible", null,
            baseline, candidate, 5, 5)
        {
            BaselineRunId = "baseline",
            CandidateRunId = "candidate",
            RunsSucceeded = true
        };
        RegressionPolicy policy = new(MetricDirection.LowerIsBetter, 5, RegressionThresholdUnit.Absolute);
        Check(RegressionGate.Evaluate(row, policy).Outcome == RegressionOutcome.Passed, "inclusive threshold");
        RegressionDecision failure = RegressionGate.Evaluate(row, policy with { MaximumRegression = 4 });
        Check(failure.Outcome == RegressionOutcome.Failed, "lower is better");
        RegressionDecision pass = RegressionGate.Evaluate(row, policy with { Direction = MetricDirection.HigherIsBetter });
        Check(pass.Outcome == RegressionOutcome.Passed, "higher is better");
        RegressionDecision unknown = RegressionGate.Evaluate(row, policy with { MinimumSamples = 7 });
        Check(unknown.Outcome == RegressionOutcome.Inconclusive &&
            RegressionGate.Evaluate(null, policy).Outcome == RegressionOutcome.Inconclusive, "missing and insufficient samples");
        Check(RegressionGate.Evaluate(row with { Baseline = baseline with { Median = 0 } },
            policy with { Unit = RegressionThresholdUnit.RelativePercent }).Code == "nonpositive_baseline", "zero baseline");
        Check(RegressionGate.Evaluate(row with { Status = "intentional" }, policy).Code == "comparability" &&
            RegressionGate.Evaluate(row with { Status = "intentional" },
                policy with { AllowIntentionalBuildDifferences = true }).Outcome == RegressionOutcome.Passed, "explicit intent");
        Check(RegressionGate.Evaluate(row,
            policy with { MaximumRegression = 0, RequireStatisticalEvidenceToFail = true }).Outcome ==
            RegressionOutcome.Inconclusive, "unconfirmed deterioration");
        HistoryDetectionReport detected = new(1, "baseline", "candidate", "latency", "higher-worse", 2, 0.05,
            "permutation", [new("latency", "ns", "release", null, "wall", "regression", "confirmed",
                baseline, candidate, 5, 5, 0.01, 0.02, 100, true)]);
        RegressionPolicy statistical = policy with { MaximumRegression = 0, RequireStatisticalEvidenceToFail = true };
        Check(RegressionGate.Evaluate(row, statistical, detected).Outcome == RegressionOutcome.Failed &&
            RegressionGate.Evaluate(row, statistical, detected with { CandidateRun = "other" }).Outcome ==
            RegressionOutcome.Inconclusive, "detection is bound to the selected pair");
        Check(RegressionGate.Aggregate([pass, unknown, failure]) == RegressionOutcome.Failed &&
            RegressionGate.Aggregate([pass, unknown]) == RegressionOutcome.Inconclusive &&
            RegressionGate.Aggregate([]) == RegressionOutcome.Inconclusive, "aggregation precedence");
        Console.WriteLine("PASS programmable History summaries and regression decisions");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new UnreachableException(message);
        }
    }
}
