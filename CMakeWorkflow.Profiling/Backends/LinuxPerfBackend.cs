using System.Diagnostics.CodeAnalysis;

namespace CMakeWorkflow.Profiling.Backends;

public sealed class LinuxPerfBackend(IProfileProcess process) : IProfilerBackend
{
    public ProfileCapabilities Capabilities { get; } = new(
        "perf", ["cpu", "scheduler", "counters"], "Linux perf PMU counters, CPU samples or scheduler tracepoints",
        "profiler-launches-target", "perf enables events before target exec",
        "recorded process and inherited children; event availability depends on perf permissions",
        ["perf.data", "CSV"]);

    [SuppressMessage("Naming", "CA1725", Justification = "Preserve the existing public parameter name for named-argument callers.")]
    public async Task<ProfileResult> CaptureAsync(ProfileRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Capabilities.Modes.Contains(request.Mode))
        {
            throw new ArgumentException($"perf does not support profile mode '{request.Mode}'.");
        }

        string raw = Path.Combine(request.OutputDirectory, "traces", "raw", request.CaptureId);
        _ = Directory.CreateDirectory(raw);
        bool counters = request.Mode == "counters";
        string path = Path.Combine(raw, counters ? "run.perf-stat.csv" : "run.perf.data");
        List<string> args = counters
            ? ["stat", "-x", ",", "-o", path, "-e", "cycles,instructions,cache-misses,branch-misses"]
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
                request.Timeout, ProfileEnvironment.Copy(request.Environment), ct);
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
