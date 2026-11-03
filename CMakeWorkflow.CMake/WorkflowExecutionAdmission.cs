namespace CMakeWorkflow;

internal static class WorkflowExecutionAdmission
{
    internal static bool ValidCpuSet(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var piece in text.Split(','))
        {
            if (piece.Length == 0 || piece.Any(c => !char.IsAsciiDigit(c) && c != '-')) return false;
            var range = piece.Split('-');
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0], out var first)
                                           || first < 0 || first > 8191) return false;
            if (range.Length == 2 && (!int.TryParse(range[1], out var last)
                                      || last < first || last > 8191)) return false;
        }

        return true;
    }

    internal static Dictionary<string, string> Require(WorkflowExecutionConstraints? constraints)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (constraints?.CpuSet is not { } requested) return result;
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Required CPU affinity cannot be guaranteed on this host.");
        var allowed = File.ReadLines("/proc/self/status")
                          .FirstOrDefault(line => line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))?
                          .Split(':', 2)[1].Trim()
                      ?? throw new IOException("Linux allowed CPU set is unavailable.");
        if (!Parse(requested).IsSubsetOf(Parse(allowed)))
            throw new InvalidOperationException($"Required CPU set {requested} is outside host allowance {allowed}.");
        var launcher = (Environment.GetEnvironmentVariable("PATH") ?? "")
                       .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                       .Select(directory => Path.Combine(directory, "taskset"))
                       .FirstOrDefault(File.Exists)
                       ?? throw new FileNotFoundException("Required CPU affinity needs the taskset launcher.");
        result["cpuSetRequested"] = requested;
        result["cpuSetAllowedAtAdmission"] = allowed;
        result["cpuAffinityLauncher"] = Path.GetFullPath(launcher);
        result["cpuAffinityGuarantee"] =
            "taskset sets affinity before target exec; no continuous host-load attestation";
        return result;
    }

    private static HashSet<int> Parse(string list)
    {
        if (!ValidCpuSet(list)) throw new InvalidDataException("Invalid observed CPU set: " + list);
        var result = new HashSet<int>();
        foreach (var piece in list.Split(','))
        {
            var bounds = piece.Split('-');
            var first = int.Parse(bounds[0]);
            var last = bounds.Length == 2 ? int.Parse(bounds[1]) : first;
            for (var cpu = first; cpu <= last; cpu++) result.Add(cpu);
        }

        return result;
    }
}