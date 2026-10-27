namespace CMakeWorkflow.Profiling.Backends;

public sealed class LinuxPerfBackend(IProfileProcess process) : IProfilerBackend
{
    public ProfileCapabilities Capabilities { get; } = new(
        "perf", ["cpu", "scheduler", "counters"], "Linux perf PMU counters, CPU samples or scheduler tracepoints",
        "profiler-launches-target", "perf enables events before target exec",
        "recorded process and inherited children; event availability depends on perf permissions",
        ["perf.data", "CSV"]);

    public async Task<ProfileResult> CaptureAsync(ProfileRequest request, CancellationToken ct)
    {
        if (!Capabilities.Modes.Contains(request.Mode))
            throw new ArgumentException($"perf does not support profile mode '{request.Mode}'.");
        var raw = Path.Combine(request.OutputDirectory, "traces", "raw", request.CaptureId);
        Directory.CreateDirectory(raw);
        var counters = request.Mode == "counters";
        var path = Path.Combine(raw, counters ? "run.perf-stat.csv" : "run.perf.data");
        var args = counters
            ? new List<string> { "stat", "-x", ",", "-o", path, "-e", "cycles,instructions,cache-misses,branch-misses" }
            : new List<string> { "record", "-o", path };
        if (request.Mode == "cpu")
            args.AddRange(["-e", "cycles:u", "--call-graph", "dwarf,16384"]);
        else if (request.Mode == "scheduler")
            args.AddRange(["-e", "sched:sched_switch", "-e", "sched:sched_wakeup", "--call-graph", "dwarf,16384"]);
        args.Add("--");
        args.Add(request.Executable);
        args.AddRange(request.Arguments);
        var start = DateTimeOffset.UtcNow;
        try
        {
            var result = await process.RunAsync("perf", args, request.WorkingDirectory,
                request.Timeout, ProfileEnvironment.Copy(request.Environment), ct);
            ProfileEnvironment.RequireSuccess(result, counters ? "perf stat" : "perf record");
            if (!File.Exists(path)) throw new IOException("perf returned success without an output file.");
            return new ProfileResult("perf", request.Mode, result.ExitCode, path, start,
                DateTimeOffset.UtcNow, "complete", Capabilities, result.Output);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }
}
