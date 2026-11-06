using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Execution;

/// <summary>Checks runtime execution constraints before a node can launch external work.</summary>
internal static class WorkflowExecutionAdmission
{
    internal static bool ValidCpuSet(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (string piece in text.Split(','))
        {
            if (piece.Length == 0 || piece.Any(c => !char.IsAsciiDigit(c) && c != '-'))
            {
                return false;
            }

            string[] range = piece.Split('-');
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0], out int first)
                                           || first < 0 || first > 8191)
            {
                return false;
            }

            if (range.Length == 2 && (!int.TryParse(range[1], out int last)
                                      || last < first || last > 8191))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Validates required CPU affinity and returns observed launch evidence.</summary>
    /// <remarks>
    /// No explicit CpuSet returns an empty map. CPU affinity requires Linux, a requested subset of the current
    /// /proc/self/status allowance, and an existing taskset on PATH. It does not launch the process, verify
    /// continuous allowance/load, or attest profiler capabilities; the extension owns profiler admission.
    /// </remarks>
    internal static Dictionary<string, string> Require(WorkflowExecutionConstraints? constraints)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        if (constraints?.CpuSet is not { } requested)
        {
            return result;
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Required CPU affinity cannot be guaranteed on this host.");
        }

        string allowed = File.ReadLines("/proc/self/status")
                          .FirstOrDefault(line => line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))?
                          .Split(':', 2)[1].Trim()
                      ?? throw new IOException("Linux allowed CPU set is unavailable.");
        if (!Parse(requested).IsSubsetOf(Parse(allowed)))
        {
            throw new InvalidOperationException($"Required CPU set {requested} is outside host allowance {allowed}.");
        }

        string launcher = (Environment.GetEnvironmentVariable("PATH") ?? "")
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
        if (!ValidCpuSet(list))
        {
            throw new InvalidDataException("Invalid observed CPU set: " + list);
        }

        HashSet<int> result = [];
        foreach (string piece in list.Split(','))
        {
            string[] bounds = piece.Split('-');
            int first = int.Parse(bounds[0], System.Globalization.CultureInfo.CurrentCulture);
            int last = bounds.Length == 2 ? int.Parse(bounds[1], System.Globalization.CultureInfo.CurrentCulture) : first;
            for (int cpu = first; cpu <= last; cpu++)
            {
                _ = result.Add(cpu);
            }
        }

        return result;
    }
}