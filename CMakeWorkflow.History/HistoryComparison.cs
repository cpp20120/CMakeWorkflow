namespace CMakeWorkflow.History;

public static class HistoryComparison
{
    private static HistoryStats Stats(List<double> values)
    {
        var sorted = values.Order().ToArray();
        var mean = sorted.Average();
        var median = sorted.Length % 2 == 0
            ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2
            : sorted[sorted.Length / 2];
        return new(sorted.Length, median, sorted[0], sorted[^1], mean,
            Math.Sqrt(sorted.Sum(x => (x - mean) * (x - mean)) / sorted.Length));
    }
    public static List<HistoryDifference> Compare(HistoryDetail baseline, HistoryDetail candidate)
    {
        static string Key(HistoryMeasurement m) => string.Join("\u001f", m.Metric, m.Variant, m.Scenario);
        var a = baseline.Measurements.GroupBy(Key).ToDictionary(x => x.Key, x => x.ToList());
        var b = candidate.Measurements.GroupBy(Key).ToDictionary(x => x.Key, x => x.ToList());
        var rows = new List<HistoryDifference>();
        if (a.Count == 0 && b.Count == 0)
            return [new("*", "", null, null, "", "missing",
                "Neither run contains indexed measurements.", null, null, null, null)];
        foreach (var key in a.Keys.Union(b.Keys).Order(StringComparer.Ordinal))
        {
            a.TryGetValue(key, out var left); b.TryGetValue(key, out var right);
            var representative = (left ?? right)![0];
            var unit = representative.Unit; var method = representative.Method;
            var status = "compatible"; string? reason = null;
            if (left is null || right is null)
            { status = "missing"; reason = left is null ? "Baseline has no measurements." : "Candidate has no measurements."; }
            else if (left.Select(x => (x.Unit, x.Method, x.Workload, x.Environment)).Distinct().Count() != 1
                || right.Select(x => (x.Unit, x.Method, x.Workload, x.Environment)).Distinct().Count() != 1
                || left[0].Unit != right[0].Unit || left[0].Method != right[0].Method)
            { status = "incompatible"; reason = "Units or measurement methods differ."; }
            else if (left[0].Workload != right[0].Workload)
            { status = "incompatible"; reason = "Recorded workload arguments differ."; }
            else if (baseline.Run.HostId is null || candidate.Run.HostId is null)
            { status = "unverified"; reason = "Stable local host identity is not recorded."; }
            else if (baseline.Run.HostId != candidate.Run.HostId)
            { status = "incompatible"; reason = "Runs were recorded on different local hosts."; }
            else if (baseline.Run.EnvironmentFingerprint is null || candidate.Run.EnvironmentFingerprint is null)
            { status = "unverified"; reason = "Host environment characteristics are missing or changed during a run."; }
            else if (baseline.Run.Environment is not { CpuModel: not null, Architecture: not null,
                LogicalProcessors: > 0, OperatingSystem: not null, KernelVersion: not null }
                || candidate.Run.Environment is not { CpuModel: not null, Architecture: not null,
                    LogicalProcessors: > 0, OperatingSystem: not null, KernelVersion: not null })
            { status = "unverified"; reason = "Essential host characteristics are missing."; }
            else if (baseline.Run.EnvironmentFingerprint != candidate.Run.EnvironmentFingerprint)
            { status = "incompatible"; reason = "Recorded host environment characteristics differ."; }
            else if (baseline.Run.Environment with { HostId = null, Fingerprint = null }
                != candidate.Run.Environment with { HostId = null, Fingerprint = null })
            { status = "incompatible"; reason = "Recorded host environment characteristics differ despite matching fingerprints."; }
            else if (left[0].Environment is null || right[0].Environment is null)
            { status = "unverified"; reason = "Build configuration or toolchain identity is not recorded."; }
            else if (left[0].Environment != right[0].Environment)
            { status = "incompatible"; reason = "Recorded build or environment characteristics differ."; }
            if (status is "compatible" or "unverified")
            {
                if (baseline.Run.ManifestHash is not null && candidate.Run.ManifestHash is not null)
                {
                    if (baseline.Run.ManifestHash != candidate.Run.ManifestHash)
                    { status = "unverified"; reason = "Workflow manifest hashes differ; comparability is unverified."; }
                }
                else if (baseline.Run.Workflow != candidate.Run.Workflow)
                { status = "unverified"; reason = "Workflow contents cannot be matched from recorded metadata."; }
            }
            var ls = left is null ? null : Stats(left.Select(x => x.Value).ToList());
            var rs = right is null ? null : Stats(right.Select(x => x.Value).ToList());
            if (status == "compatible")
                reason = "Recorded characteristics match; frequencies and background load are not attested. Descriptive comparison only.";
            var change = status == "compatible" ? rs!.Median - ls!.Median : (double?)null;
            rows.Add(new(representative.Metric, unit, representative.Variant,
                representative.Scenario, method, status, reason, ls, rs, change,
                change is not null && ls!.Median != 0 ? change / ls.Median * 100 : null));
        }
        return rows;
    }
}
