namespace CMakeWorkflow.Profiling.Backends;

public sealed class MacInstrumentsBackend(IProfileProcess process) : IProfilerBackend
{
    public ProfileCapabilities Capabilities { get; } = new(
        "instruments", ["cpu", "scheduler", "allocations"],
        "Instruments Time Profiler, System Trace or Allocations",
        "profiler-launches-target", "xctrace launches after tracing setup",
        "recorded process; template-specific system events may include other processes",
        [".trace"]);

    public async Task<ProfileResult> CaptureAsync(ProfileRequest request, CancellationToken ct)
    {
        var template = request.Mode switch
        {
            "cpu" => "Time Profiler", "scheduler" => "System Trace",
            "allocations" => "Allocations", _ => throw new ArgumentException($"Unsupported Instruments mode: {request.Mode}")
        };
        var raw = Path.Combine(request.OutputDirectory, "traces", "raw", request.CaptureId);
        Directory.CreateDirectory(raw);
        var path = Path.Combine(raw, "run.trace");
        var targetOutput = Path.Combine(request.OutputDirectory, request.CaptureId + ".target.stdout.log");
        var args = new List<string> { "xctrace", "record", "--template", template,
            "--output", path, "--no-prompt", "--time-limit",
            $"{Math.Max(1, (int)Math.Ceiling(request.Timeout.TotalSeconds))}s",
            "--target-stdout", targetOutput };
        foreach (var (key, value) in request.Environment)
            args.AddRange(["--env", key + "=" + value]);
        args.AddRange(["--launch", "--", request.Executable]);
        args.AddRange(request.Arguments);
        var started = DateTimeOffset.UtcNow;
        try
        {
            var result = await process.RunAsync("xcrun", args, request.WorkingDirectory,
                request.Timeout + TimeSpan.FromSeconds(30), null, ct);
            ProfileEnvironment.RequireSuccess(result, "xctrace record");
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new IOException("xctrace returned success without a .trace artifact.");
            return new ProfileResult("instruments", request.Mode, result.ExitCode, path,
                started, DateTimeOffset.UtcNow, "complete", Capabilities, result.Output);
        }
        catch
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }
}
