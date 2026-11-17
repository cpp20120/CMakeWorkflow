using CMakeWorkflow.History.Models;

namespace CMakeWorkflow.History.Analysis;

internal sealed record HistoryHostRow(string RunId, string? HostId, double? BuildSeconds,
    double? CacheHitRate, string? CacheCounterScope, double? PeakRssBytes, double? CpuSeconds,
    double? MemoryStalls, double? ArtifactBytes)
{
    internal static HistoryHostRow From(HistoryDetail detail)
    {
        double? Sum(string metric)
        {
            double[] values = [.. detail.Measurements.Where(m => m.Metric == metric).Select(m => m.Value)];
            return values.Length == 0 ? null : values.Sum();
        }

        HistoryMeasurement[] counters = [.. detail.Measurements.Where(m =>
            m.Metric is "compiler_cache_hits" or "compiler_cache_misses")];
        string[] methods = [.. counters.Select(m => m.Method).Distinct(StringComparer.Ordinal)];
        bool complete = counters.Length != 0 && counters.GroupBy(m => m.NodeId).All(node =>
            node.Count(m => m.Metric == "compiler_cache_hits") == 1
            && node.Count(m => m.Metric == "compiler_cache_misses") == 1);
        double? hits = Sum("compiler_cache_hits");
        double? total = hits + Sum("compiler_cache_misses");
        double? rate = complete && methods.Length == 1 && total is > 0 ? hits / total : null;
        return new(detail.Run.Id, detail.Run.HostId, Sum("build_wall_time"), rate,
            methods.Length == 1 ? methods[0] : methods.Length == 0 ? null : "mixed",
            null, null, null, Sum("artifact_result_bytes"));
    }
}
