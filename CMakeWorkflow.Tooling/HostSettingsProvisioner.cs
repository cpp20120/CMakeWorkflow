using System.Globalization;

namespace CMakeWorkflow.Tooling;

// Explicit, typed Linux host mutations. No shell, sudo, or arbitrary paths from profiles.
public sealed record HostSettingChange(string Id, string Path, string? Before, string After);

public interface IHostSettingsBackend
{
    string? Read(string path);
    void Write(string path, string value);
}

public sealed class LinuxHostSettingsBackend : IHostSettingsBackend
{
    public string? Read(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    public void Write(string path, string value)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux host settings only.");
        File.WriteAllText(path, value + "\n");
    }
}

public static class HostSettingsProvisioner
{
    private const string PerfPath = "/proc/sys/kernel/perf_event_paranoid";
    private const string PolicyRoot = "/sys/devices/system/cpu/cpufreq/";
    private const string PolicySuffix = "/scaling_governor";

    public static IReadOnlyList<HostSettingChange> Plan(HostRequirements requirements, IHostSettingsBackend backend,
        IReadOnlyList<string>? cpuPaths = null)
    {
        var changes = new List<HostSettingChange>();
        if (requirements.MaxPerfEventParanoid is int max)
        {
            var current = backend.Read(PerfPath);
            if (!int.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                throw new InvalidOperationException("Cannot inspect perf_event_paranoid; global settings are not writable without verification.");
            if (n > max) changes.Add(new("perf_event_paranoid", PerfPath, current, max.ToString(CultureInfo.InvariantCulture)));
        }
        if (requirements.CpuGovernor is string expected)
        {
            if (cpuPaths is null) throw new InvalidOperationException("CPU governor targets must be enumerated explicitly.");
            foreach (var path in cpuPaths.Order(StringComparer.Ordinal))
            {
                ValidatePolicyPath(path);
                var current = backend.Read(path) ?? throw new InvalidOperationException("Cannot inspect governor: " + path);
                if (current is not ("performance" or "powersave" or "schedutil" or "ondemand" or "conservative" or "userspace"))
                    throw new InvalidOperationException("Unknown current governor: " + current);
                if (current != expected) changes.Add(new("cpu_governor", path, current, expected));
            }
        }
        return changes;
    }

    private static void ValidatePolicyPath(string path)
    {
        if (!path.StartsWith(PolicyRoot, StringComparison.Ordinal) ||
            !path.EndsWith(PolicySuffix, StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected CPU governor policy path.");
        var policy = path[PolicyRoot.Length..^PolicySuffix.Length];
        if (!policy.StartsWith("policy", StringComparison.Ordinal) ||
            !uint.TryParse(policy.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            policy.Length == 6 || policy.AsSpan(6).IndexOf('/') >= 0)
            throw new InvalidDataException("Unexpected CPU governor policy path.");
    }

    public static IReadOnlyList<string> EnumerateCpuPaths()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (!Directory.Exists(PolicyRoot)) throw new InvalidOperationException("cpufreq policies unavailable.");
        return Directory.EnumerateDirectories(PolicyRoot, "policy*")
            .Select(p => Path.Combine(p, "scaling_governor"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static void Apply(IReadOnlyList<HostSettingChange> changes, IHostSettingsBackend backend,
        CancellationToken ct)
    {
        // Preflight the entire approved set before any write; sysfs changes are not transactional.
        if (changes.GroupBy(c => c.Path, StringComparer.Ordinal).Any(g => g.Count() != 1))
            throw new InvalidDataException("Duplicate host setting path.");
        foreach (var change in changes)
        {
            if (change.Path != PerfPath) ValidatePolicyPath(change.Path);
            if (change.Id == "perf_event_paranoid" && change.Path != PerfPath ||
                change.Id == "cpu_governor" && change.Path == PerfPath ||
                change.Id is not ("cpu_governor" or "perf_event_paranoid"))
                throw new InvalidDataException("Unexpected system setting identifier.");
            if (change.Before is null || !string.Equals(backend.Read(change.Path), change.Before, StringComparison.Ordinal))
                throw new InvalidOperationException("Host state changed since approval: " + change.Id);
        }
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested();
            backend.Write(change.Path, change.After);
            if (backend.Read(change.Path) != change.After)
                throw new IOException("Host setting did not retain requested value: " + change.Id);
        }
    }
}
