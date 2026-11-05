using System.Diagnostics.CodeAnalysis;

namespace CMakeWorkflow.Profiling.Backends;

public sealed class WindowsEtwBackend(IProfileProcess process) : IProfilerBackend
{
    public ProfileCapabilities Capabilities { get; } = new(
        "etw", ["cpu", "scheduler"], "ETW CPU sampled and context-switch events",
        "recorder-starts-before-target", "WPR start completes before target launch",
        "system-wide recording; select target process and its threads during analysis",
        [".etl"]);

    [SuppressMessage("Naming", "CA1725", Justification = "Preserve the existing public parameter name for named-argument callers.")]
    public async Task<ProfileResult> CaptureAsync(ProfileRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Capabilities.Modes.Contains(request.Mode))
        {
            throw new ArgumentException($"WPR does not support profile mode '{request.Mode}'.");
        }

        string raw = Path.Combine(request.OutputDirectory, "traces", "raw", request.CaptureId);
        _ = Directory.CreateDirectory(raw);
        string session = "CMakeWorkflow_" + Guid.NewGuid().ToString("N");
        string trace = Path.Combine(raw, session + ".etl");
        string lockDirectory = Path.Combine(Path.GetTempPath(), "cmake-workflow-wpr");
        _ = Directory.CreateDirectory(lockDirectory);
        FileStream? lease = null;
        bool started = false;
        DateTimeOffset began = DateTimeOffset.UtcNow;
        while (lease is null)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                lease = new FileStream(Path.Combine(lockDirectory, "capture.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33 or 35)
            {
                await Task.Delay(100, ct);
            }
        }

        await using FileStream captureLease = lease;
        try
        {
            // A unique WPR instance name means cleanup cannot stop an unrelated
            // recording. The mutex coordinates our own capture processes.
            CommandResult status = await process.RunAsync("wpr", ["-status", "-instancename", session],
                request.WorkingDirectory, TimeSpan.FromSeconds(15), null, ct);
            if (status.Output.Contains("recording is in progress", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"WPR session name is already active: {session}");
            }

            string profile = request.Mode == "cpu" ? "CPU.light" : "CPU";
            // A canceled start can leave a live WPR session even if the child
            // process has not returned. Cancel only our unique instance.
            started = true;
            CommandResult start = await process.RunAsync("wpr",
                ["-start", profile, "-filemode", "-instancename", session],
                request.WorkingDirectory, TimeSpan.FromSeconds(30), null, ct);
            ProfileEnvironment.RequireSuccess(start, "wpr -start");
            DateTimeOffset targetStartedAt = DateTimeOffset.UtcNow;
            CommandResult target = await process.RunAsync(request.Executable, request.Arguments,
                request.WorkingDirectory, request.Timeout,
                ProfileEnvironment.Copy(request.Environment), ct);
            DateTimeOffset targetFinishedAt = DateTimeOffset.UtcNow;
            CommandResult stop = await process.RunAsync("wpr",
                ["-stop", trace, "-instancename", session],
                request.WorkingDirectory, TimeSpan.FromMinutes(2), null, CancellationToken.None);
            ProfileEnvironment.RequireSuccess(stop, "wpr -stop");
            started = false;
            return !File.Exists(trace)
                ? throw new IOException("WPR returned success without an ETL artifact.")
                : new ProfileResult("etw", request.Mode, target.ExitCode, trace,
                began, DateTimeOffset.UtcNow, target.TimedOut ? "targetTimedOut" : "complete",
                Capabilities, target.Output + stop.Output,
                target.ProcessId, targetStartedAt, targetFinishedAt);
        }
        finally
        {
            if (started)
            {
                try
                {
                    _ = await process.RunAsync("wpr", ["-cancel", "-instancename", session],
                        request.WorkingDirectory, TimeSpan.FromSeconds(30), null, CancellationToken.None);
                }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
                catch (Exception error)
                {
                    await Console.Error.WriteLineAsync($"[CMakeWorkflow] WPR cleanup for {session} failed: {error.Message}");
                }
#pragma warning restore CA1031

            }

        }
    }
}