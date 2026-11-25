namespace CMakeWorkflow.Contracts.History;

/// <summary>Descriptive statistics including population standard deviation.</summary>
public sealed record HistorySampleSummary(int Count, double Median, double Min, double Max, double Mean,
    double StandardDeviation)
{
    /// <summary>Returns null for an empty sequence. Rejects non-finite samples and unrepresentable summaries.</summary>
    public static HistorySampleSummary? Calculate(IEnumerable<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        double[] sorted = [.. samples.Order()];
        if (sorted.Length == 0)
        {
            return null;
        }
        if (sorted.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("A history sample must be finite.", nameof(samples));
        }

        double mean = sorted.Average();
        int middle = sorted.Length / 2;
        double median = sorted.Length % 2 == 0 ? (sorted[middle - 1] / 2) + (sorted[middle] / 2) : sorted[middle];
        double deviation = Math.Sqrt(sorted.Sum(value => (value - mean) * (value - mean)) / sorted.Length);
        return double.IsFinite(mean) && double.IsFinite(deviation)
            ? new(sorted.Length, median, sorted[0], sorted[^1], mean, deviation)
            : throw new ArgumentException("The history sample summary exceeds the supported numeric range.", nameof(samples));
    }
}
