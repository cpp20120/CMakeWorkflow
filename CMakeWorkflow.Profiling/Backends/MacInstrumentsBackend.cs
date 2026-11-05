using System.Diagnostics.CodeAnalysis;

namespace CMakeWorkflow.Profiling.Backends;

public sealed class MacInstrumentsBackend(IProfileProcess process) : IProfilerBackend
{
    public ProfileCapabilities Capabilities { get; } = new(
        "instruments", ["cpu", "scheduler", "allocations"],
        "Instruments Time Profiler, System Trace or Allocations",
        "profiler-launches-target", "xctrace launches after tracing setup",
        "recorded process; template-specific system events may include other processes",
        [".trace"]);

    [SuppressMessage("Naming", "CA1725", Justification = "Preserve the existing public parameter name for named-argument callers.")]
    public async Task<ProfileResult> CaptureAsync(ProfileRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string template = request.Mode switch
        {
            "cpu" => "Time Profiler",
            "scheduler" => "System Trace",
            "allocations" => "Allocations",
            _ => throw new ArgumentException($"Unsupported Instruments mode: {request.Mode}")
        };
        string raw = Path.Combine(request.OutputDirectory, "traces", "raw", request.CaptureId);
        _ = Directory.CreateDirectory(raw);
        string path = Path.Combine(raw, "run.trace");
        string targetOutput = Path.Combine(request.OutputDirectory, request.CaptureId + ".target.stdout.log");
        List<string> args =
        [
            "xctrace", "record", "--template", template,
            "--output", path, "--no-prompt", "--time-limit",
            $"{Math.Max(1, (int)Math.Ceiling(request.Timeout.TotalSeconds))}s",
            "--target-stdout", targetOutput
        ];
        foreach ((string? key, string? value) in request.Environment)
        {
            args.AddRange(["--env", key + "=" + value]);
        }

        args.AddRange(["--launch", "--", request.Executable]);
        args.AddRange(request.Arguments);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            CommandResult result = await process.RunAsync("xcrun", args, request.WorkingDirectory,
                request.Timeout + TimeSpan.FromSeconds(30), null, ct);
            ProfileEnvironment.RequireSuccess(result, "xctrace record");
            return !File.Exists(path) && !Directory.Exists(path)
                ? throw new IOException("xctrace returned success without a .trace artifact.")
                : new ProfileResult("instruments", request.Mode, result.ExitCode, path,
                started, DateTimeOffset.UtcNow, "complete", Capabilities, result.Output);
        }
        catch
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            throw;
        }
    }
}