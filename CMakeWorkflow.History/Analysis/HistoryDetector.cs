using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.History.Models;

namespace CMakeWorkflow.History.Analysis;

// This is a one-sided test for a user-declared regression direction. It does
// not claim that sequential benchmark samples are independent or free of drift.
public static class HistoryDetector
{
    private const int MinimumSamples = 10;
    private const int MonteCarloPermutations = 9999;
    private const int MaximumExactPermutations = 200000;

    public static HistoryDetectionReport Detect(HistoryDetail baseline,
        HistoryDetail candidate, string metric, string? variant, string? scenario,
        string direction,
        double minimumEffectPercent, double alpha, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        int sign = direction switch
        {
            "higher-worse" => 1,
            "lower-worse" => -1,
            _ => throw new ArgumentException(
                "--direction must be higher-worse or lower-worse.")
        };
        if (string.IsNullOrWhiteSpace(metric))
        {
            throw new ArgumentException("--metric requires a metric name.");
        }

        if (!double.IsFinite(minimumEffectPercent) || minimumEffectPercent <= 0)
        {
            throw new ArgumentException("--min-percent must be a positive finite number.");
        }

        if (!double.IsFinite(alpha) || alpha is <= 0 or >= 1)
        {
            throw new ArgumentException("--alpha must be between zero and one.");
        }

        List<HistoryDetection> results = [];
        foreach (HistoryDifference? difference in HistoryComparison.Compare(baseline, candidate)
                     .Where(x => x.Metric == metric && (variant is null || x.Variant == variant)
                                                    && (scenario is null || x.Scenario == scenario)))
        {
            ct.ThrowIfCancellationRequested();
            string status = difference.Status;
            string reason = difference.Reason ?? "";
            double? p = null;
            int? permutations = null;
            bool? exact = null;
            if (status == "compatible")
            {
                if (baseline.Run.Status != "succeeded" || candidate.Run.Status != "succeeded")
                {
                    status = "unverified";
                    reason = "Both runs must have succeeded.";
                }
                else if (!baseline.Run.SourceAvailable || !candidate.Run.SourceAvailable)
                {
                    status = "unverified";
                    reason = "An authoritative run directory is missing.";
                }
                else
                {
                    List<HistoryMeasurement> left = Samples(baseline, difference);
                    List<HistoryMeasurement> right = Samples(candidate, difference);
                    if (left.Count < MinimumSamples || right.Count < MinimumSamples)
                    {
                        status = "insufficient";
                        reason = $"At least {MinimumSamples} individual samples per run are required.";
                    }
                    else if (left.Select(x => x.SampleIndex).Distinct().Count() != left.Count
                             || right.Select(x => x.SampleIndex).Distinct().Count() != right.Count)
                    {
                        status = "unverified";
                        reason = "Individual sample indices are duplicated.";
                    }
                    else if (difference.Baseline!.Median == 0 || difference.PercentChange is null)
                    {
                        status = "unverified";
                        reason = "A zero baseline median cannot support a percentage effect threshold.";
                    }
                    else
                    {
                        int seed = Seed(baseline.Run.Id, candidate.Run.Id, difference);
                        (double PValue, int Permutations, bool Exact) = PermutationTest([.. left.Select(x => x.Value)],
                            [.. right.Select(x => x.Value)], sign, seed, ct);
                        p = PValue;
                        permutations = Permutations;
                        exact = Exact;
                        status = "inconclusive";
                        reason = "Evidence has not met both the adjusted p-value and minimum effect requirements.";
                    }
                }
            }

            results.Add(new(difference.Metric, difference.Unit, difference.Variant,
                difference.Scenario, difference.Method, status, reason,
                difference.Baseline, difference.Candidate, difference.AbsoluteChange,
                difference.PercentChange, p, null, permutations, exact));
        }

        if (results.Count == 0)
        {
            results.Add(new(metric, "", variant, scenario, "", "missing",
                "Neither run contains this metric/variant/scenario selection.", null, null, null, null,
                null, null, null, null));
        }

        // Holm correction controls family-wise false positives across all
        // tested variant/scenario rows in this command invocation.
        (HistoryDetection row, int index)[] tested = [.. results.Select((row, index) => (row, index))
            .Where(x => x.row.PValue is not null)
            .OrderBy(x => x.row.PValue).ThenBy(x => x.index)];
        double adjustedFloor = 0.0;
        for (int i = 0; i < tested.Length; i++)
        {
            (HistoryDetection? row, int index) = tested[i];
            adjustedFloor = Math.Max(adjustedFloor,
                Math.Min(1, row.PValue!.Value * (tested.Length - i)));
            double effect = row.PercentChange!.Value * sign;
            bool regression = adjustedFloor <= alpha && effect >= minimumEffectPercent;
            results[index] = row with
            {
                AdjustedPValue = adjustedFloor,
                Status = regression ? "regression" : "inconclusive",
                Reason = regression
                    ? "One-sided permutation test and practical effect threshold both passed. "
                      + "Independence, thermal drift and background load remain unverified."
                    : "Evidence has not met both the adjusted p-value and minimum effect requirements."
            };
        }

        return new(1, baseline.Run.Id, candidate.Run.Id, metric, direction,
            minimumEffectPercent, alpha, "one-sided permutation test of median difference; Holm correction",
            results);
    }

    private static List<HistoryMeasurement> Samples(HistoryDetail run, HistoryDifference key) =>
        [.. run.Measurements.Where(x => x.Metric == key.Metric && x.Variant == key.Variant
                                                           && x.Scenario == key.Scenario).OrderBy(x => x.SampleIndex)];

    private static int Seed(string baseline, string candidate, HistoryDifference key)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f",
            baseline, candidate, key.Metric, key.Variant, key.Scenario)));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static (double PValue, int Permutations, bool Exact) PermutationTest(
        double[] baseline, double[] candidate, int sign, int seed, CancellationToken ct)
    {
        double[] pooled = [.. baseline, .. candidate];
        double[] left = new double[baseline.Length];
        double[] right = new double[candidate.Length];
        double observed = sign * (Median(candidate) - Median(baseline));
        int extreme = 0;
        int count = 0;
        if (CombinationCount(pooled.Length, baseline.Length) <= MaximumExactPermutations)
        {
            int[] chosen = [.. Enumerable.Range(0, baseline.Length)];
            bool[] mask = new bool[pooled.Length];
            while (true)
            {
                if ((count & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                Array.Clear(mask);
                foreach (int index in chosen)
                {
                    mask[index] = true;
                }

                int li = 0;
                int ri = 0;
                for (int i = 0; i < pooled.Length; i++)
                {
                    if (mask[i])
                    {
                        left[li++] = pooled[i];
                    }
                    else
                    {
                        right[ri++] = pooled[i];
                    }
                }

                if (sign * (Median(right) - Median(left)) >= observed)
                {
                    extreme++;
                }

                count++;
                int position = chosen.Length - 1;
                while (position >= 0 && chosen[position] == pooled.Length - chosen.Length + position)
                {
                    position--;
                }

                if (position < 0)
                {
                    break;
                }

                chosen[position]++;
                for (int i = position + 1; i < chosen.Length; i++)
                {
                    chosen[i] = chosen[i - 1] + 1;
                }
            }

            return ((double)extreme / count, count, true);
        }

        Random random = new(seed);
        double[] shuffled = (double[])pooled.Clone();
        for (int iteration = 0; iteration < MonteCarloPermutations; iteration++)
        {
            if ((iteration & 1023) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            for (int i = shuffled.Length - 1; i > 0; i--)
            {

#pragma warning disable CA5394 // Seeded randomness makes statistical permutation tests reproducible; it is not used for security.
                int swap = random.Next(i + 1);
#pragma warning restore CA5394

                (shuffled[i], shuffled[swap]) = (shuffled[swap], shuffled[i]);
            }

            Array.Copy(shuffled, 0, left, 0, left.Length);
            Array.Copy(shuffled, left.Length, right, 0, right.Length);
            if (sign * (Median(right) - Median(left)) >= observed)
            {
                extreme++;
            }
        }

        return ((double)(extreme + 1) / (MonteCarloPermutations + 1),
            MonteCarloPermutations, false);
    }

    private static double CombinationCount(int total, int selected)
    {
        selected = Math.Min(selected, total - selected);
        double count = 1.0;
        for (int i = 1; i <= selected; i++)
        {
            count *= (total - selected + i) / (double)i;
            if (count > MaximumExactPermutations)
            {
                return count;
            }
        }

        return count;
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        return values.Length % 2 == 0
            ? (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2
            : values[values.Length / 2];
    }
}