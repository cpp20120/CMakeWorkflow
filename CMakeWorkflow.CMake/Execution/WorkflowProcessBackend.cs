using System.ComponentModel;
using System.Diagnostics;

namespace CMakeWorkflow;

// A neutral process lifecycle backend. Callers prepare the process and stream
// destinations; this type only starts, drains, waits for and cleans up the child.
internal static class WorkflowProcessBackend
{
    internal sealed record Specification(ProcessStartInfo StartInfo, Stream StandardOutput,
        Stream StandardError);

    internal sealed record Outcome(int? ExitCode, Exception? StartError);

    public static async Task<Outcome> RunAsync(Specification specification, CancellationToken ct,
        Action<TimeSpan>? heartbeat, Action<TimeSpan> durationCompleted)
    {
        var processTimer = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(specification.StartInfo)!;
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        {
            return new(null, error);
        }
        if (process is null)
            return new(null, new IOException("Process.Start returned null."));

        using (process)
        {
            // Preserve the existing Windows Job Object supervision mechanism.
            using var windowsJob = OperatingSystem.IsWindows() ? WindowsProcessJob.Attach(process) : null;
            process.StandardInput.Close();
            using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeatTask = heartbeat is null
                ? Task.CompletedTask
                : Task.Run(async () =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                    try
                    {
                        while (await timer.WaitForNextTickAsync(heartbeatStop.Token))
                        {
                            try
                            {
                                heartbeat(processTimer.Elapsed);
                            }
                            catch (IOException)
                            {
                                /* Observability must not interrupt cleanup. */
                            }
                            catch (ObjectDisposedException)
                            {
                                /* Closed console/log sink. */
                            }
                        }
                    }
                    catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested)
                    {
                    }
                });
            var copies = Task.WhenAll(
                process.StandardOutput.BaseStream.CopyToAsync(specification.StandardOutput),
                process.StandardError.BaseStream.CopyToAsync(specification.StandardError));
            try
            {
                await process.WaitForExitAsync(ct);
                await copies.WaitAsync(ct);
                return new(process.ExitCode, null);
            }
            finally
            {
                processTimer.Stop();
                heartbeatStop.Cancel();
                await heartbeatTask;
                durationCompleted(processTimer.Elapsed);
                if (windowsJob is not null && (ct.IsCancellationRequested || !process.HasExited))
                {
                    await windowsJob.TerminateAndWaitAsync(CancellationToken.None);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                else if (windowsJob is null && !process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                    }

                    await process.WaitForExitAsync(CancellationToken.None);
                }

                if (!copies.IsCompleted)
                {
                    process.StandardOutput.Close();
                    process.StandardError.Close();
                }

                try
                {
                    await copies;
                }
                catch (Exception error) when (ct.IsCancellationRequested &&
                                              error is IOException or ObjectDisposedException)
                {
                }
            }
        }
    }
}
