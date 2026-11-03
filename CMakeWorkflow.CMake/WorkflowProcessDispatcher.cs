using System.ComponentModel;
using System.Diagnostics;

namespace CMakeWorkflow;

// Shared process ownership boundary for workflow DAG nodes and research commands.
// Build-tree ownership and Windows Job Objects remain in the CMake workflow layer.
public static class WorkflowProcessDispatcher
{
    public static async Task<int> RunAsync(NodeResult result,
        IReadOnlyDictionary<string, string> environment, string output, CancellationToken ct,
        Action<TimeSpan>? heartbeat = null)
    {
        var launcher = result.ExecutionConditions.GetValueOrDefault("cpuAffinityLauncher");
        var info = new ProcessStartInfo(launcher ?? result.Executable!)
        {
            UseShellExecute = false, WorkingDirectory = result.WorkingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        if (launcher is not null)
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(result.ExecutionConditions["cpuSetRequested"]);
            info.ArgumentList.Add(result.Executable!);
        }
        foreach (var argument in result.Arguments) info.ArgumentList.Add(argument);
        foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        BuildTreeOwnership.PropagateTo(info);
        await using var stdout = File.Create(Path.Combine(output, result.Stdout!));
        await using var stderr = File.Create(Path.Combine(output, result.Stderr!));
        ct.ThrowIfCancellationRequested();
        var processTimer = Stopwatch.StartNew();
        Process process;
        try { process = Process.Start(info) ?? throw new MissingWorkflowToolException(result.Executable!,
            new IOException("Process.Start returned null.")); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        { throw new MissingWorkflowToolException(result.Executable!, error); }
        using (process)
        {
        // Windows Kill(entireProcessTree:true) does not wait for grandchildren.
        // A Job Object retains custody of descendants even if Ninja exits first.
        using var windowsJob = OperatingSystem.IsWindows() ? WindowsProcessJob.Attach(process) : null;
        process.StandardInput.Close();
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = heartbeat is null ? Task.CompletedTask : Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            try
            {
                while (await timer.WaitForNextTickAsync(heartbeatStop.Token))
                {
                    try { heartbeat(processTimer.Elapsed); }
                    catch (IOException) { /* Observability must not interrupt cleanup. */ }
                    catch (ObjectDisposedException) { /* Closed console/log sink. */ }
                }
            }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
        });
        var copies = Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(stdout),
            process.StandardError.BaseStream.CopyToAsync(stderr));
        try
        {
            await process.WaitForExitAsync(ct);
            await copies.WaitAsync(ct);
            return process.ExitCode;
        }
        finally
        {
            processTimer.Stop();
            heartbeatStop.Cancel();
            await heartbeatTask;
            result.ProcessDurationSeconds = processTimer.Elapsed.TotalSeconds;
            if (windowsJob is not null && (ct.IsCancellationRequested || !process.HasExited))
            {
                // Terminate descendants only on cancellation/failure. Successful
                // executions must never query or terminate their job in cleanup.
                // Await the entire job, not only the direct Ninja/CMake process.
                await windowsJob.TerminateAndWaitAsync(CancellationToken.None);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            else if (windowsJob is null && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            // A detached descendant can keep pipes open after the direct child exits.
            // Closing readers also makes cancellation bounded in that case.
            if (!copies.IsCompleted)
            {
                process.StandardOutput.Close();
                process.StandardError.Close();
            }
            try { await copies; }
            catch (Exception error) when (ct.IsCancellationRequested && error is IOException or ObjectDisposedException) { }
        }
        }
    }

}
