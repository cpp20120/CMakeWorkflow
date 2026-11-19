using CMakeWorkflow.Core;
using CMakeWorkflow.Profiling.Capture;

namespace CMakeWorkflow.Profiling.Backends;

public sealed class LinuxPerfBackend(IProfileProcess process) : IProfilerBackend
{
    public ProfileCapabilities Capabilities { get; } = new(
        "perf", ["cpu", "scheduler", "counters"], "Linux perf PMU counters, CPU samples or scheduler tracepoints",
        "profiler-launches-target", "perf enables events before target exec",
        "recorded process and inherited children; event availability depends on perf permissions",
        ["perf.data", "CSV"]);

    public async Task<ProfileResult> CaptureAsync(ProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Capabilities.Modes.Contains(request.Mode))
        {
            throw new ArgumentException($"perf does not support profile mode '{request.Mode}'.");
        }

        if (request.MemoryStallEvent is not (null or "cycle_activity.stalls_mem_any"
            or "cycle_activity.stalls_mem_any:u" or "cycle_activity.stalls_mem_any:k" or "ex_no_retire.load_not_complete"
            or "ex_no_retire.load_not_complete:u" or "ex_no_retire.load_not_complete:k")
            || (request.MemoryStallEvent is not null && request.Mode != "counters"))
        {
            throw new ArgumentException("Memory stalls require a supported explicit perf counters event.");
        }

        string raw = Path.Combine(request.OutputDirectory, "traces", "raw", request.CaptureId);
        _ = Directory.CreateDirectory(raw);
        bool counters = request.Mode == "counters";
        string path = Path.Combine(raw, counters ? "run.perf-stat.csv" : "run.perf.data");
        string events = "cycles,instructions,cache-misses,branch-misses";
        if (request.MemoryStallEvent is { } memoryEvent)
        {
            events += "," + memoryEvent;
        }
        List<string> args = counters
            ? ["stat", "-x", ",", "-o", path, "-e", events]
            : ["record", "-o", path];
        if (request.Mode == "cpu")
        {
            args.AddRange([
                "-e", "cycles:u",
                "--user-callchains",
                "--call-graph", "dwarf,16384"
            ]);
        }
        else if (request.Mode == "scheduler")
        {
            args.AddRange(["-e", "sched:sched_switch", "-e", "sched:sched_wakeup", "--call-graph", "dwarf,16384"]);
        }

        args.Add("--");
        if (request.CpuSet is not null)
        {
            if (request.CpuAffinityLauncher is null)
            {
                throw new InvalidOperationException("Required CPU affinity launcher was not admitted.");
            }

            args.Add(request.CpuAffinityLauncher);
            args.Add("-c");
            args.Add(request.CpuSet);
        }

        args.Add(request.Executable);
        args.AddRange(request.Arguments);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        try
        {
            CommandResult result = await process.RunAsync("perf", args, request.WorkingDirectory,
                request.Timeout, ProfileEnvironment.Copy(request.Environment), cancellationToken);
            ProfileEnvironment.RequireSuccess(result, counters ? "perf stat" : "perf record");
            return !File.Exists(path)
                ? throw new IOException("perf returned success without an output file.")
                : new ProfileResult("perf", request.Mode, result.ExitCode, path, start,
                DateTimeOffset.UtcNow, "complete", Capabilities, result.Output);
        }
        catch
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            throw;
        }
    }
}
