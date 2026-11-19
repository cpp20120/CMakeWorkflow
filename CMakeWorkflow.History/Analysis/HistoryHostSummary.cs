using CMakeWorkflow.History.Models;

namespace CMakeWorkflow.History.Analysis;

internal sealed record HistoryHostRow(string RunId, string? HostId, double? BuildSeconds,
    double? CacheHitRate, string? CacheCounterScope, double? PeakRssBytes, double? CpuSeconds,
    double? MemoryStalls, double? ArtifactBytes, string? ResourceMethod, string? MemoryStallEvent,
    string? MemoryStallUnit, string? MemoryStallMethod)
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
        HistoryMeasurement[] resources = [.. detail.Measurements.Where(m =>
            m.Metric is "peak_rss_bytes" or "cpu_time")];
        string[] resourceMethods = [.. resources.Select(m => m.Method).Distinct(StringComparer.Ordinal)];
        double[] peaks = [.. resources.Where(m => m.Metric == "peak_rss_bytes").Select(m => m.Value)];
        HistoryMeasurement[] stalls = [.. detail.Measurements.Where(m =>
            (m.Method == "perf stat" && m.Metric is "cycle_activity.stalls_mem_any"
                or "cycle_activity.stalls_mem_any:u" or "cycle_activity.stalls_mem_any:k"
                or "ex_no_retire.load_not_complete" or "ex_no_retire.load_not_complete:u"
                or "ex_no_retire.load_not_complete:k")
            || (m.Method?.StartsWith("etw-pmc-overflow;", StringComparison.Ordinal) == true
                && m.Metric.StartsWith("memory_stalls:", StringComparison.Ordinal)))];
        bool comparableStalls = stalls.Select(m => (m.Metric, m.Unit, m.Method, m.Variant, m.Workload, m.Environment))
            .Distinct().Count() == 1;
        string? resourceMethod = resourceMethods.Length == 1 ? resourceMethods[0]
            : resourceMethods.Length == 0 ? null : "mixed";
        string? stallEvent = comparableStalls ? stalls[0].Metric : stalls.Length == 0 ? null : "mixed";
        return new(detail.Run.Id, detail.Run.HostId, Sum("build_wall_time"), rate,
            methods.Length == 1 ? methods[0] : methods.Length == 0 ? null : "mixed",
            resourceMethods.Length == 1 && peaks.Length > 0 ? peaks.Max() : null,
            resourceMethods.Length == 1 ? Sum("cpu_time") : null,
            comparableStalls ? stalls.Sum(m => m.Value) : null, Sum("artifact_result_bytes"),
            resourceMethod, stallEvent, comparableStalls ? stalls[0].Method == "perf stat" ? "cycles" : stalls[0].Unit : null,
            comparableStalls ? stalls[0].Method : null);
    }
}
