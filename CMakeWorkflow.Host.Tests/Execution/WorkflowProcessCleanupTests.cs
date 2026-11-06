using System.Diagnostics;
using CMakeWorkflow.Core.Processes;

namespace CMakeWorkflow.Host.Tests.Execution;

internal static class WorkflowProcessCleanupTests
{
    private static readonly string AssemblyPath = typeof(WorkflowProcessCleanupTests).Assembly.Location;
    private static readonly string DotNet = Environment.ProcessPath
                                            ?? throw new InvalidOperationException("The .NET host path is unavailable.");

    public static async Task RunAsync(string root, string scenario)
    {
        if (scenario is "all" or "duration")
        {
            await DurationObserverFailure(root);
        }

        if (scenario is "all" or "blocking-duration")
        {
            await BlockingDurationObserver(root);
        }

        if (scenario is "all" or "heartbeat")
        {
            await HeartbeatObserverFailure(root);
        }

        if (scenario is "all" or "streams")
        {
            await StreamCancellation(root);
        }

        if (scenario is "all" or "sink")
        {
            await SinkFailureOverlapsCancellation(root);
        }

        if (scenario is "all" or "sink-live")
        {
            await SinkFailureWithoutCancellation(root);
        }

        if (scenario is "all" or "descendant")
        {
            await DescendantCancellation(root);
        }

        if (scenario is "all" or "drain")
        {
            await CancellationDuringPostExitDrain(root);
        }

        if (scenario is "all" or "partial")
        {
            await PartialPumpSetupFailure(root);
        }

        if (scenario is not ("all" or "duration" or "blocking-duration" or "heartbeat" or "streams"
            or "sink" or "sink-live" or "descendant" or "drain" or "partial"))
        {
            throw new ArgumentException("Unknown cleanup test scenario: " + scenario);
        }

        Console.WriteLine("Workflow process cleanup checks passed: " + scenario);
    }

    private static async Task DurationObserverFailure(string root)
    {
        Fixture fixture = CreateFixture(root, "duration");
        using CancellationTokenSource cancellation = new();
        Task<WorkflowProcessBackend.Outcome> execution = Run(fixture, null,
            _ => throw new InvalidOperationException("duration observer failure"), cancellation: cancellation.Token);
        try
        {
            await WaitForTree(fixture);
            await cancellation.CancelAsync();
            Exception failure = await CaptureFailure(execution);
            await RequireTreeStopped(fixture,
                "Duration callback failure bypassed child and descendant cleanup.");
            AssertFailureOrder(failure,
                error => error is OperationCanceledException,
                error => error is InvalidOperationException { Message: "duration observer failure" });
        }
        finally { await StopFixture(fixture, cancellation, execution); }
    }

    private static async Task HeartbeatObserverFailure(string root)
    {
        Fixture fixture = CreateFixture(root, "heartbeat");
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource callbackFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WorkflowProcessBackend.Outcome> execution = Run(fixture, elapsed =>
        {
            _ = callbackFailed.TrySetResult();
            throw new InvalidOperationException("heartbeat observer failure");
        }, _ => throw new InvalidOperationException("duration observer failure"), cancellation: cancellation.Token);
        try
        {
            await WaitForTree(fixture);
            await callbackFailed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await cancellation.CancelAsync();
            Exception failure = await CaptureFailure(execution);
            await RequireTreeStopped(fixture,
                "Heartbeat task failure bypassed child and descendant cleanup.");
            AssertFailureOrder(failure,
                error => error is OperationCanceledException,
                error => error is InvalidOperationException { Message: "heartbeat observer failure" },
                error => error is InvalidOperationException { Message: "duration observer failure" });
        }
        finally { await StopFixture(fixture, cancellation, execution); }
    }

    private static async Task BlockingDurationObserver(string root)
    {
        Fixture fixture = CreateFixture(root, "blocking-duration");
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim release = new();
        TaskCompletionSource callbackEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WorkflowProcessBackend.Outcome> execution = Run(fixture, null, elapsed =>
        {
            _ = callbackEntered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20)))
            {
                throw new TimeoutException("Duration callback release timed out.");
            }

            throw new InvalidOperationException("blocked duration observer failure");
        }, cancellation: cancellation.Token);
        try
        {
            await WaitForTree(fixture);
            await cancellation.CancelAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await RequireTreeStopped(fixture,
                "Process cleanup waited for a blocked duration callback.");
            release.Set();
            Exception failure = await CaptureFailure(execution);
            AssertFailureOrder(failure,
                error => error is OperationCanceledException,
                error => error is InvalidOperationException { Message: "blocked duration observer failure" });
        }
        finally
        {
            release.Set();
            await StopFixture(fixture, cancellation, execution);
        }
    }

    private static async Task StreamCancellation(string root)
    {
        Fixture fixture = CreateFixture(root, "streams", flood: true);
        using CancellationTokenSource cancellation = new();
        Task<WorkflowProcessBackend.Outcome> execution = Run(fixture, null, _ => { }, cancellation: cancellation.Token);
        try
        {
            await WaitForTree(fixture);
            await cancellation.CancelAsync();
            Exception failure = await CaptureFailure(execution);
            await RequireTreeStopped(fixture,
                "Cancellation during concurrent output streaming left a process running.");
            if (failure is not OperationCanceledException cancelled
                || cancelled.CancellationToken != cancellation.Token)
            {
                throw new UnreachableException("Stream cancellation lost its cancellation exception.", failure);
            }

            if (!File.Exists(fixture.Stdout) || !File.Exists(fixture.Stderr))
            {
                throw new UnreachableException("Output stream destinations were not created.");
            }
        }
        finally { await StopFixture(fixture, cancellation, execution); }
    }

    private static async Task SinkFailureOverlapsCancellation(string root)
    {
        Fixture fixture = CreateFixture(root, "sink", flood: true);
        using CancellationTokenSource cancellation = new();
        CancellationOverlapSink sink = new();

#pragma warning disable CA2025 // Run owns the sink and awaits backend completion before disposal; this test drains execution in finally.
        Task<WorkflowProcessBackend.Outcome> execution = Run(fixture, null, _ => { }, sink, cancellation: cancellation.Token);
#pragma warning restore CA2025

        try
        {
            await WaitForTree(fixture);
            await sink.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancellation.CancelAsync();
            await sink.PumpStopRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            sink.ReleaseFailure();
            Exception failure = await CaptureFailure(execution);
            await RequireTreeStopped(fixture,
                "A stream-copy failure overlapping cancellation bypassed process cleanup.");
            AssertFailureOrder(failure,
                error => error is OperationCanceledException,
                error => error is IOException { Message: "injected sink failure" });
        }
        finally
        {
            sink.ReleaseFailure();
            await StopFixture(fixture, cancellation, execution);
        }
    }

    private static async Task SinkFailureWithoutCancellation(string root)
    {
        Fixture fixture = CreateFixture(root, "sink-live", flood: true);
        ThrowingSink sink = new();
        using CancellationTokenSource cancellation = new();

#pragma warning disable CA2025 // Run owns the sink and awaits backend completion before disposal; this test drains execution in finally.
        Task<WorkflowProcessBackend.Outcome> execution = Run(fixture, null, _ => { }, sink, cancellation: cancellation.Token);
#pragma warning restore CA2025

        try
        {
            await WaitForTree(fixture);
            await sink.Failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            sink.ReleaseFailure();
            Exception failure = await CaptureFailure(execution);
            await RequireTreeStopped(fixture,
                "A destination write failure did not terminate the child and descendant.");
            if (failure is not IOException { Message: "injected sink failure" })
            {
                throw new UnreachableException("No-cancellation sink failure changed its primary exception.", failure);
            }
        }
        finally
        {
            sink.ReleaseFailure();
            await StopFixture(fixture, cancellation, execution);
        }
    }

    private static async Task DescendantCancellation(string root)
    {
        Fixture fixture = CreateFixture(root, "descendant");
        using CancellationTokenSource cancellation = new();
        Task<WorkflowProcessBackend.Outcome> execution = Run(fixture, null, _ => { }, cancellation: cancellation.Token);
        try
        {
            await WaitForTree(fixture);
            await cancellation.CancelAsync();
            Exception failure = await CaptureFailure(execution);
            await RequireTreeStopped(fixture,
                "Cancellation did not terminate both the child and its descendant.");
            if (failure is not OperationCanceledException cancelled
                || cancelled.CancellationToken != cancellation.Token)
            {
                throw new UnreachableException("Descendant cleanup changed cancellation into another failure.", failure);
            }
        }
        finally { await StopFixture(fixture, cancellation, execution); }
    }

    private static async Task CancellationDuringPostExitDrain(string root)
    {
        Fixture fixture = CreateFixture(root, "drain");
        CancellationSink gate = new();
        using CancellationTokenSource cancellation = new();
        bool durationReported = false;
        ProcessStartInfo start = new(DotNet)
        {
            UseShellExecute = false,
            WorkingDirectory = fixture.Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string? argument in new[] { AssemblyPath, "--workflow-process-worker", "exit-output", fixture.Pids })
        {
            start.ArgumentList.Add(argument);
        }

        await using FileStream stderr = File.Create(fixture.Stderr);
        Task<WorkflowProcessBackend.Outcome> execution = WorkflowProcessBackend.RunAsync(new WorkflowProcessBackend.Specification(start,
            gate, stderr), null, _ =>
        {
            durationReported = true;
        }, ct: cancellation.Token);
        try
        {
            await gate.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForProcessExit(fixture.Pids, TimeSpan.FromSeconds(10));
            await cancellation.CancelAsync();
            await gate.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Exception failure = await CaptureFailure(execution);
            if (failure is not OperationCanceledException || !durationReported)
            {
                throw new UnreachableException("Cancellation during post-exit output draining did not complete safely.", failure);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            try { _ = await execution.WaitAsync(TimeSpan.FromSeconds(5)); }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
            catch (Exception) { }
#pragma warning restore CA1031

            await gate.DisposeAsync();
        }
    }

    private static async Task PartialPumpSetupFailure(string root)
    {
        Fixture fixture = CreateFixture(root, "partial");
        ProcessStartInfo start = new(DotNet)
        {
            UseShellExecute = false,
            WorkingDirectory = fixture.Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false
        };
        foreach (string? argument in new[] { AssemblyPath, "--workflow-process-worker", "wait-pid",
                     fixture.Pids, fixture.Ready })
        {
            start.ArgumentList.Add(argument);
        }

        using ManualResetEventSlim allowCopyStart = new();
        await using ReadinessSink stdout = new(allowCopyStart);
        await using FileStream stderr = File.Create(fixture.Stderr);
        Task<WorkflowProcessBackend.Outcome> execution = Task.Run(() => WorkflowProcessBackend.RunAsync(
            new WorkflowProcessBackend.Specification(start, stdout, stderr), null, _ => { }, ct: CancellationToken.None));
        try
        {
            await WaitUntil(() => File.Exists(fixture.Ready) && File.Exists(fixture.Pids),
                TimeSpan.FromSeconds(10), "Partial-pump fixture did not start.");
            allowCopyStart.Set();
            Exception failure = await CaptureFailure(execution);
            int pid = int.Parse(await File.ReadAllTextAsync(fixture.Pids, CancellationToken.None), System.Globalization.CultureInfo.CurrentCulture);
            await WaitUntil(() => !IsRunning(pid), TimeSpan.FromSeconds(5),
                "A setup failure after starting stdout capture left the process running.");
            Exception primary = failure is AggregateException aggregate
                ? aggregate.InnerExceptions[0]
                : failure;
            if (primary is not InvalidOperationException)
            {
                throw new UnreachableException("Missing stderr redirection did not preserve its setup failure.", failure);
            }
        }
        finally
        {
            allowCopyStart.Set();
            if (File.Exists(fixture.Pids))
            {
                Stop(int.Parse(await File.ReadAllTextAsync(fixture.Pids, CancellationToken.None), System.Globalization.CultureInfo.CurrentCulture));
            }

            try { _ = await execution.WaitAsync(TimeSpan.FromSeconds(5)); }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
            catch (Exception) { }
#pragma warning restore CA1031

        }
    }

    private static async Task<WorkflowProcessBackend.Outcome> Run(Fixture fixture,
        Action<TimeSpan>? heartbeat, Action<TimeSpan> duration,
        Stream? stdoutSink = null, CancellationToken cancellation = default)
    {
        ProcessStartInfo start = new(DotNet)
        {
            UseShellExecute = false,
            WorkingDirectory = fixture.Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string? argument in new[] { AssemblyPath, "--workflow-process-worker",
                     fixture.Flood ? "spawn-flood" : "spawn", fixture.Pids, fixture.Ready })
        {
            start.ArgumentList.Add(argument);
        }

        await using Stream stdout = stdoutSink ?? File.Create(fixture.Stdout);
        await using FileStream stderr = File.Create(fixture.Stderr);
        return await WorkflowProcessBackend.RunAsync(new WorkflowProcessBackend.Specification(start,
            stdout, stderr), heartbeat, duration, ct: cancellation);
    }

    private static Fixture CreateFixture(string root, string name, bool flood = false)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        return new Fixture(directory, Path.Combine(directory, "pids"), Path.Combine(directory, "ready"),
            Path.Combine(directory, "stdout"), Path.Combine(directory, "stderr"), flood);
    }

    private static async Task WaitForTree(Fixture fixture)
    {
        await WaitUntil(() =>
        {
            if (!File.Exists(fixture.Pids) || !File.Exists(fixture.Ready))
            {
                return false;
            }

            string[] parts = File.ReadAllText(fixture.Pids).Split('|');
            return parts.Length == 2 && int.TryParse(parts[0], out int parent)
                                     && int.TryParse(parts[1], out int child)
                                     && IsRunning(parent) && IsRunning(child);
        }, TimeSpan.FromSeconds(20), "Process fixture failed to start its parent and descendant.");
    }

    private static async Task WaitForProcessExit(string pidPath, TimeSpan timeout)
    {
        int pid = 0;
        await WaitUntil(() =>
        {
            return File.Exists(pidPath) && int.TryParse(File.ReadAllText(pidPath), out pid) && !IsRunning(pid);
        }, timeout, "Process worker did not exit before output-drain cancellation.");
    }

    private static async Task RequireTreeStopped(Fixture fixture, string message)
    {
        (int parent, int child) = ReadPids(fixture);
        await WaitUntil(() => !IsRunning(parent) && !IsRunning(child), TimeSpan.FromSeconds(5), message);
    }

    private static async Task<Exception> CaptureFailure(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(15)); }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
        catch (Exception error) { return error; }
#pragma warning restore CA1031

        throw new UnreachableException("Expected process execution to fail after cancellation/observer failure.");
    }

    private static void AssertFailureOrder(Exception failure, params Func<Exception, bool>[] expected)
    {
        if (failure is not AggregateException aggregate || aggregate.InnerExceptions.Count < expected.Length)
        {
            throw new UnreachableException("Expected ordered primary and secondary process failures.", failure);
        }

        for (int index = 0; index < expected.Length; index++)
        {
            if (!expected[index](aggregate.InnerExceptions[index]))
            {
                throw new UnreachableException($"Process failure {index} had an unexpected type or order.", aggregate);
            }
        }
    }

    private static async Task StopFixture(Fixture fixture, CancellationTokenSource cancellation, Task execution)
    {
        await cancellation.CancelAsync();
        try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
        catch (Exception) { }
#pragma warning restore CA1031

        if (File.Exists(fixture.Pids))
        {
            (int parent, int child) = ReadPids(fixture);
            Stop(parent);
            Stop(child);
        }
    }

    private static (int Parent, int Child) ReadPids(Fixture fixture)
    {
        string[] parts = File.ReadAllText(fixture.Pids).Split('|');
        return parts.Length != 2 || !int.TryParse(parts[0], out int parent)
                                 || !int.TryParse(parts[1], out int child)
            ? throw new UnreachableException("Invalid process fixture PID file.")
            : ((int Parent, int Child))(parent, child);
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout, string message)
    {
        Stopwatch until = Stopwatch.StartNew();
        while (until.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }
        throw new TimeoutException(message);
    }

    private static bool IsRunning(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            string stat = Path.Combine("/proc", pid.ToString(System.Globalization.CultureInfo.CurrentCulture), "stat");
            if (!File.Exists(stat))
            {
                return false;
            }

            string details = File.ReadAllText(stat);
            int stateOffset = details.LastIndexOf(')') + 2;
            return stateOffset > 1 && details[stateOffset] != 'Z';
        }
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void Stop(int pid)
    {
        try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
        catch (ArgumentException) { }
    }

    private sealed record Fixture(string Directory, string Pids, string Ready, string Stdout,
        string Stderr, bool Flood);

    private sealed class ThrowingSink : MemoryStream
    {
        private readonly TaskCompletionSource releaseFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Failed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFailure() => releaseFailure.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _ = Failed.TrySetResult();
            await releaseFailure.Task;
            throw new IOException("injected sink failure");
        }
    }

    private sealed class CancellationOverlapSink : MemoryStream
    {
        private readonly TaskCompletionSource releaseFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PumpStopRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseFailure() => releaseFailure.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _ = WriteEntered.TrySetResult();
            using CancellationTokenRegistration registration = cancellationToken.Register(() => PumpStopRequested.TrySetResult());
            await releaseFailure.Task;
            throw new IOException("injected sink failure");
        }
    }

    private sealed class CancellationSink : MemoryStream
    {
        public TaskCompletionSource WriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _ = WriteEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            {
                _ = StopRequested.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ReadinessSink(ManualResetEventSlim allowCopyStart) : MemoryStream
    {
        private int waited;

        public override bool CanWrite => Interlocked.Exchange(ref waited, 1) == 0
                                         && !allowCopyStart.Wait(TimeSpan.FromSeconds(10))
            ? throw new TimeoutException("Test did not release the copy setup barrier.")
            : base.CanWrite;
    }
}