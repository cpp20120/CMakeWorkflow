using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Core.Processes;

/// <summary>Best-effort bounded observations owned by the backend's process lifetime.</summary>
internal sealed class ProcessResourceSampler(Process process)
{
    private const int IntervalMilliseconds = 50;
    private const int MaximumProcesses = 4096;
    private readonly long ticksPerSecond = OperatingSystem.IsLinux() ? Sysconf(2) : 0; // _SC_CLK_TCK
    private int samples;
    private long? peakRss;
    private double? cpuSeconds;

    internal ProcessResourceUsage? Result => samples == 0 ? null : new(1,
        OperatingSystem.IsLinux() ? "visible-process-tree" : "direct-process",
        OperatingSystem.IsLinux() ? "procfs sampled RSS and cumulative CPU estimates"
            : "System.Diagnostics.Process sampled working set and CPU",
        IntervalMilliseconds, samples, peakRss, cpuSeconds);

    internal async Task ObserveAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(IntervalMilliseconds));
        try
        {
            do
            {
                try
                {
                    if (OperatingSystem.IsLinux())
                    {
                        await ObserveLinuxAsync(ct);
                    }
                    else
                    {
                        process.Refresh();
                        Record(process.WorkingSet64, process.TotalProcessorTime.TotalSeconds);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or Win32Exception or InvalidOperationException or NotSupportedException or FormatException
                    or OverflowException)
                {
                    // A process can exit between discovery and any metadata read.
                }
            }
            while (!process.HasExited && await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private async Task ObserveLinuxAsync(CancellationToken ct)
    {
        Queue<(int Pid, int? Parent)> pending = new();
        HashSet<int> visited = [];
        pending.Enqueue((process.Id, null));
        long rss = 0;
        double cpu = 0;
        int observed = 0;
        while (pending.TryDequeue(out (int Pid, int? Parent) current) && visited.Count < MaximumProcesses)
        {
            int pid = current.Pid;
            if (!visited.Add(pid))
            {
                continue;
            }

            try
            {
                string root = "/proc/" + pid.ToString(CultureInfo.InvariantCulture);
                string stat = await File.ReadAllTextAsync(root + "/stat", ct);
                int end = stat.LastIndexOf(')');
                if (end < 0)
                {
                    continue;
                }
                string[] fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 22 || (current.Parent is int parent
                    && int.Parse(fields[1], CultureInfo.InvariantCulture) != parent))
                {
                    continue;
                }
                // utime/stime plus CPU of descendants already waited for. Live
                // descendants contribute through their own rows in this traversal.
                double ticks = fields.Skip(11).Take(4).Sum(value => double.Parse(value, CultureInfo.InvariantCulture));
                long pages = long.Parse(fields[21], CultureInfo.InvariantCulture);
                rss = checked(rss + (Math.Max(0, pages) * Environment.SystemPageSize));
                cpu += ticks;
                observed++;
                foreach (string task in Directory.EnumerateDirectories(root + "/task"))
                {
                    string children = await File.ReadAllTextAsync(Path.Combine(task, "children"), ct);
                    foreach (string child in children.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        pending.Enqueue((int.Parse(child, CultureInfo.InvariantCulture), pid));
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        if (observed > 0)
        {
            Record(rss, ticksPerSecond > 0 ? cpu / ticksPerSecond : null);
        }
    }

    private void Record(long rss, double? cpu)
    {
        samples++;
        peakRss = Math.Max(peakRss ?? 0, rss);
        if (cpu is >= 0 && double.IsFinite(cpu.Value))
        {
            cpuSeconds = Math.Max(cpuSeconds ?? 0, cpu.Value);
        }
    }

    [DllImport("libc", EntryPoint = "sysconf", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long Sysconf(int name);
}
