using System.Diagnostics;
using CMakeWorkflow;

internal static class WorkflowProcessCleanupTests
{
    private static readonly string AssemblyPath = typeof(WorkflowProcessCleanupTests).Assembly.Location;
    private static readonly string DotNet = Environment.ProcessPath
        ?? throw new InvalidOperationException("The .NET host path is unavailable.");

    public static async Task RunAsync(string root, string scenario)
    {
        if (scenario is "all" or "duration") await DurationObserverFailure(root);
        if (scenario is "all" or "blocking-duration") await BlockingDurationObserver(root);
        if (scenario is "all" or "heartbeat") await HeartbeatObserverFailure(root);
        if (scenario is "all" or "streams") await StreamCancellation(root);
        if (scenario is "all" or "sink") await SinkFailureOverlapsCancellation(root);
        if (scenario is "all" or "sink-live") await SinkFailureWithoutCancellation(root);
        if (scenario is "all" or "descendant") await DescendantCancellation(root);
        if (scenario is "all" or "drain") await CancellationDuringPostExitDrain(root);
        if (scenario is "all" or "partial") await PartialPumpSetupFailure(root);
        if (scenario is not ("all" or "duration" or "blocking-duration" or "heartbeat" or "streams"
            or "sink" or "sink-live" or "descendant" or "drain" or "partial"))
            throw new ArgumentException("Unknown cleanup test scenario: " + scenario);
        Console.WriteLine("Workflow process cleanup checks passed: " + scenario);
    }

    private static async Task DurationObserverFailure(string root)
    {
        var fixture = CreateFixture(root, "duration");
        using var cancellation = new CancellationTokenSource();
        var execution = Run(fixture, cancellation.Token, null,
            _ => throw new InvalidOperationException("duration observer failure"));
        try
        {
            await WaitForTree(fixture);
            cancellation.Cancel();
            var failure = await CaptureFailure(execution);
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
        var fixture = CreateFixture(root, "heartbeat");
        using var cancellation = new CancellationTokenSource();
        var callbackFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = Run(fixture, cancellation.Token, _ =>
        {
            callbackFailed.TrySetResult();
            throw new InvalidOperationException("heartbeat observer failure");
        }, _ => throw new InvalidOperationException("duration observer failure"));
        try
        {
            await WaitForTree(fixture);
            await callbackFailed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            cancellation.Cancel();
            var failure = await CaptureFailure(execution);
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
        var fixture = CreateFixture(root, "blocking-duration");
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = Run(fixture, cancellation.Token, null, _ =>
        {
            callbackEntered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("Duration callback release timed out.");
            throw new InvalidOperationException("blocked duration observer failure");
        });
        try
        {
            await WaitForTree(fixture);
            cancellation.Cancel();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await RequireTreeStopped(fixture,
                "Process cleanup waited for a blocked duration callback.");
            release.Set();
            var failure = await CaptureFailure(execution);
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
        var fixture = CreateFixture(root, "streams", flood: true);
        using var cancellation = new CancellationTokenSource();
        var execution = Run(fixture, cancellation.Token, null, _ => { });
        try
        {
            await WaitForTree(fixture);
            cancellation.Cancel();
            var failure = await CaptureFailure(execution);
            await RequireTreeStopped(fixture,
                "Cancellation during concurrent output streaming left a process running.");
            if (failure is not OperationCanceledException cancelled
                || cancelled.CancellationToken != cancellation.Token)
                throw new Exception("Stream cancellation lost its cancellation exception.", failure);
            if (!File.Exists(fixture.Stdout) || !File.Exists(fixture.Stderr))
                throw new Exception("Output stream destinations were not created.");
        }
        finally { await StopFixture(fixture, cancellation, execution); }
    }

    private static async Task SinkFailureOverlapsCancellation(string root)
    {
        var fixture = CreateFixture(root, "sink", flood: true);
        using var cancellation = new CancellationTokenSource();
        var sink = new CancellationOverlapSink();
        var execution = Run(fixture, cancellation.Token, null, _ => { }, sink);
        try
        {
            await WaitForTree(fixture);
            await sink.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await sink.PumpStopRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            sink.ReleaseFailure();
            var failure = await CaptureFailure(execution);
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
        var fixture = CreateFixture(root, "sink-live", flood: true);
        var sink = new ThrowingSink();
        using var cancellation = new CancellationTokenSource();
        var execution = Run(fixture, cancellation.Token, null, _ => { }, sink);
        try
        {
            await WaitForTree(fixture);
            await sink.Failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            sink.ReleaseFailure();
            var failure = await CaptureFailure(execution);
            await RequireTreeStopped(fixture,
                "A destination write failure did not terminate the child and descendant.");
            if (failure is not IOException { Message: "injected sink failure" })
                throw new Exception("No-cancellation sink failure changed its primary exception.", failure);
        }
        finally
        {
            sink.ReleaseFailure();
            await StopFixture(fixture, cancellation, execution);
        }
    }

    private static async Task DescendantCancellation(string root)
    {
        var fixture = CreateFixture(root, "descendant");
        using var cancellation = new CancellationTokenSource();
        var execution = Run(fixture, cancellation.Token, null, _ => { });
        try
        {
            await WaitForTree(fixture);
            cancellation.Cancel();
            var failure = await CaptureFailure(execution);
            await RequireTreeStopped(fixture,
                "Cancellation did not terminate both the child and its descendant.");
            if (failure is not OperationCanceledException cancelled
                || cancelled.CancellationToken != cancellation.Token)
                throw new Exception("Descendant cleanup changed cancellation into another failure.", failure);
        }
        finally { await StopFixture(fixture, cancellation, execution); }
    }

    private static async Task CancellationDuringPostExitDrain(string root)
    {
        var fixture = CreateFixture(root, "drain");
        var gate = new CancellationSink();
        using var cancellation = new CancellationTokenSource();
        var durationReported = false;
        var start = new ProcessStartInfo(DotNet)
        {
            UseShellExecute = false,
            WorkingDirectory = fixture.Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { AssemblyPath, "--workflow-process-worker", "exit-output", fixture.Pids })
            start.ArgumentList.Add(argument);
        await using var stderr = File.Create(fixture.Stderr);
        var execution = WorkflowProcessBackend.RunAsync(new WorkflowProcessBackend.Specification(start,
            gate, stderr), cancellation.Token, null, _ =>
        {
            durationReported = true;
        });
        try
        {
            await gate.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForProcessExit(fixture.Pids, TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await gate.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var failure = await CaptureFailure(execution);
            if (failure is not OperationCanceledException || !durationReported)
                throw new Exception("Cancellation during post-exit output draining did not complete safely.", failure);
        }
        finally
        {
            cancellation.Cancel();
            try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
            await gate.DisposeAsync();
        }
    }

    private static async Task PartialPumpSetupFailure(string root)
    {
        var fixture = CreateFixture(root, "partial");
        var start = new ProcessStartInfo(DotNet)
        {
            UseShellExecute = false,
            WorkingDirectory = fixture.Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false
        };
        foreach (var argument in new[] { AssemblyPath, "--workflow-process-worker", "wait-pid",
                     fixture.Pids, fixture.Ready })
            start.ArgumentList.Add(argument);
        using var allowCopyStart = new ManualResetEventSlim();
        await using var stdout = new ReadinessSink(allowCopyStart);
        await using var stderr = File.Create(fixture.Stderr);
        var execution = Task.Run(() => WorkflowProcessBackend.RunAsync(
            new WorkflowProcessBackend.Specification(start, stdout, stderr), CancellationToken.None, null, _ => { }));
        try
        {
            await WaitUntil(() => File.Exists(fixture.Ready) && File.Exists(fixture.Pids),
                TimeSpan.FromSeconds(10), "Partial-pump fixture did not start.");
            allowCopyStart.Set();
            var failure = await CaptureFailure(execution);
            var pid = int.Parse(File.ReadAllText(fixture.Pids));
            await WaitUntil(() => !IsRunning(pid), TimeSpan.FromSeconds(5),
                "A setup failure after starting stdout capture left the process running.");
            var primary = failure is AggregateException aggregate
                ? aggregate.InnerExceptions[0]
                : failure;
            if (primary is not InvalidOperationException)
                throw new Exception("Missing stderr redirection did not preserve its setup failure.", failure);
        }
        finally
        {
            allowCopyStart.Set();
            if (File.Exists(fixture.Pids)) Stop(int.Parse(File.ReadAllText(fixture.Pids)));
            try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
        }
    }

    private static async Task<WorkflowProcessBackend.Outcome> Run(Fixture fixture,
        CancellationToken cancellation, Action<TimeSpan>? heartbeat, Action<TimeSpan> duration,
        Stream? stdoutSink = null)
    {
        var start = new ProcessStartInfo(DotNet)
        {
            UseShellExecute = false,
            WorkingDirectory = fixture.Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { AssemblyPath, "--workflow-process-worker",
                     fixture.Flood ? "spawn-flood" : "spawn", fixture.Pids, fixture.Ready })
            start.ArgumentList.Add(argument);
        await using var stdout = stdoutSink ?? File.Create(fixture.Stdout);
        await using var stderr = File.Create(fixture.Stderr);
        return await WorkflowProcessBackend.RunAsync(new WorkflowProcessBackend.Specification(start,
            stdout, stderr), cancellation, heartbeat, duration);
    }

    private static Fixture CreateFixture(string root, string name, bool flood = false)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        return new Fixture(directory, Path.Combine(directory, "pids"), Path.Combine(directory, "ready"),
            Path.Combine(directory, "stdout"), Path.Combine(directory, "stderr"), flood);
    }

    private static async Task WaitForTree(Fixture fixture)
    {
        await WaitUntil(() =>
        {
            if (!File.Exists(fixture.Pids) || !File.Exists(fixture.Ready)) return false;
            var parts = File.ReadAllText(fixture.Pids).Split('|');
            return parts.Length == 2 && int.TryParse(parts[0], out var parent)
                   && int.TryParse(parts[1], out var child)
                   && IsRunning(parent) && IsRunning(child);
        }, TimeSpan.FromSeconds(20), "Process fixture failed to start its parent and descendant.");
    }

    private static async Task WaitForProcessExit(string pidPath, TimeSpan timeout)
    {
        int pid = 0;
        await WaitUntil(() =>
        {
            if (!File.Exists(pidPath) || !int.TryParse(File.ReadAllText(pidPath), out pid)) return false;
            return !IsRunning(pid);
        }, timeout, "Process worker did not exit before output-drain cancellation.");
    }

    private static async Task RequireTreeStopped(Fixture fixture, string message)
    {
        var (parent, child) = ReadPids(fixture);
        await WaitUntil(() => !IsRunning(parent) && !IsRunning(child), TimeSpan.FromSeconds(5), message);
    }

    private static async Task<Exception> CaptureFailure(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception error) { return error; }
        throw new Exception("Expected process execution to fail after cancellation/observer failure.");
    }

    private static void AssertFailureOrder(Exception failure, params Func<Exception, bool>[] expected)
    {
        if (failure is not AggregateException aggregate || aggregate.InnerExceptions.Count < expected.Length)
            throw new Exception("Expected ordered primary and secondary process failures.", failure);
        for (var index = 0; index < expected.Length; index++)
            if (!expected[index](aggregate.InnerExceptions[index]))
                throw new Exception($"Process failure {index} had an unexpected type or order.", aggregate);
    }

    private static async Task StopFixture(Fixture fixture, CancellationTokenSource cancellation, Task execution)
    {
        cancellation.Cancel();
        try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { }
        if (File.Exists(fixture.Pids))
        {
            var (parent, child) = ReadPids(fixture);
            Stop(parent);
            Stop(child);
        }
    }

    private static (int Parent, int Child) ReadPids(Fixture fixture)
    {
        var parts = File.ReadAllText(fixture.Pids).Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var parent)
                               || !int.TryParse(parts[1], out var child))
            throw new Exception("Invalid process fixture PID file.");
        return (parent, child);
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout, string message)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < timeout)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException(message);
    }

    private static bool IsRunning(int pid)
    {
        if (pid <= 0) return false;
        if (OperatingSystem.IsLinux())
        {
            var stat = Path.Combine("/proc", pid.ToString(), "stat");
            if (!File.Exists(stat)) return false;
            var details = File.ReadAllText(stat);
            var stateOffset = details.LastIndexOf(')') + 2;
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
            Failed.TrySetResult();
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
            WriteEntered.TrySetResult();
            using var registration = cancellationToken.Register(() => PumpStopRequested.TrySetResult());
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
            WriteEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            {
                StopRequested.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ReadinessSink(ManualResetEventSlim allowCopyStart) : MemoryStream
    {
        private int waited;

        public override bool CanWrite
        {
            get
            {
                if (Interlocked.Exchange(ref waited, 1) == 0
                    && !allowCopyStart.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Test did not release the copy setup barrier.");
                return base.CanWrite;
            }
        }
    }
}
