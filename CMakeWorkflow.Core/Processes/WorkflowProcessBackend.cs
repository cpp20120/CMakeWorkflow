using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace CMakeWorkflow;

// A neutral process lifecycle backend. Callers prepare the process and stream
// destinations; this type only starts, drains, waits for and cleans up the child.
internal static class WorkflowProcessBackend
{
    private const int CopyBufferSize = 81920;

    internal sealed record Specification(ProcessStartInfo StartInfo, Stream StandardOutput,
        Stream StandardError);

    internal sealed record Outcome(int? ExitCode, Exception? StartError);

    public static async Task<Outcome> RunAsync(Specification specification, CancellationToken ct,
        Action<TimeSpan>? heartbeat, Action<TimeSpan> durationCompleted)
    {
        var processTimer = Stopwatch.StartNew();
        var stdoutCopy = new CopyState();
        var stderrCopy = new CopyState();
        var observerFailures = new List<Exception>();
        var cleanupFailures = new List<Exception>();
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

        WindowsProcessJob? windowsJob = null;
        CancellationTokenSource? heartbeatStop = null;
        CancellationTokenSource? operationWaitStop = null;
        CancellationTokenSource? pumpStop = null;
        Task heartbeatTask = Task.CompletedTask;
        Task? processWait = null;
        Task? copiesTask = null;
        var copiesTaskObserved = false;
        var durationEligible = false;
        var cleanupStarted = 0;
        Exception? primaryFailure = null;
        Outcome? outcome = null;
        var elapsed = TimeSpan.Zero;

        try
        {
            // Preserve Windows Job Object supervision. Setup failures after
            // Process.Start are covered by the same cleanup path as execution.
            windowsJob = OperatingSystem.IsWindows() ? WindowsProcessJob.Attach(process) : null;
            process.StandardInput.Close();
            var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            heartbeatStop = heartbeatCancellation;
            var operationWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            operationWaitStop = operationWaitCancellation;
            var pumpCancellation = new CancellationTokenSource();
            pumpStop = pumpCancellation;

            if (heartbeat is not null)
            {
                heartbeatTask = Task.Run(async () =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                    try
                    {
                        while (await timer.WaitForNextTickAsync(heartbeatCancellation.Token))
                        {
                            try
                            {
                                heartbeat(processTimer.Elapsed);
                            }
                            catch (IOException)
                            {
                                /* Existing behavior treats a closed output sink as benign. */
                            }
                            catch (ObjectDisposedException)
                            {
                                /* Existing behavior treats a closed output sink as benign. */
                            }
                        }
                    }
                    catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
                    {
                    }
                });
            }

            // Start and retain each bounded pump separately. If stderr setup
            // fails, stdout is still known to cleanup and await.
            stdoutCopy.Task = PumpAsync(process.StandardOutput.BaseStream,
                specification.StandardOutput, pumpCancellation.Token, stdoutCopy,
                () => Volatile.Read(ref cleanupStarted) != 0);
            stderrCopy.Task = PumpAsync(process.StandardError.BaseStream,
                specification.StandardError, pumpCancellation.Token, stderrCopy,
                () => Volatile.Read(ref cleanupStarted) != 0);
            var copyJoin = Task.WhenAll(stdoutCopy.Task!, stderrCopy.Task!);
            copiesTask = copyJoin;
            durationEligible = true;
            var processExit = process.WaitForExitAsync(operationWaitCancellation.Token);
            processWait = processExit;
            var processExited = false;
            var stdoutDone = false;
            var stderrDone = false;
            while (!processExited || !stdoutDone || !stderrDone)
            {
                var waiters = new List<Task>(4);
                if (!processExited) waiters.Add(processExit);
                if (!stdoutDone) waiters.Add(stdoutCopy.Task!);
                if (!stderrDone) waiters.Add(stderrCopy.Task!);
                Task completed;
                try { completed = await Task.WhenAny(waiters).WaitAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw;
                }
                ct.ThrowIfCancellationRequested();

                if (!processExited && ReferenceEquals(completed, processExit))
                {
                    await processExit;
                    processExited = true;
                }
                else if (!stdoutDone && ReferenceEquals(completed, stdoutCopy.Task))
                {
                    await stdoutCopy.Task;
                    stdoutDone = true;
                }
                else if (!stderrDone && ReferenceEquals(completed, stderrCopy.Task))
                {
                    await stderrCopy.Task;
                    stderrDone = true;
                }
            }

            // Keep the original concurrent-copy completion contract and
            // cancellation behavior after the fail-fast lifecycle monitoring.
            await copyJoin.WaitAsync(ct);
            copiesTaskObserved = true;
            outcome = new(process.ExitCode, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { ct.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException callerCancellation) { primaryFailure = callerCancellation; }
        }
        catch (Exception error)
        {
            primaryFailure = error;
        }

        elapsed = processTimer.Elapsed;
        processTimer.Stop();

        // First stop waiters and pumps, then clean up the child and streams.
        // Observer callbacks are deferred until all safety cleanup has run.
        Volatile.Write(ref cleanupStarted, 1);
        TryCancel(heartbeatStop, cleanupFailures);
        TryCancel(operationWaitStop, cleanupFailures);

        var processExitedDuringCleanup = false;
        var terminationSucceeded = true;
        try
        {
            processExitedDuringCleanup = process.HasExited;
        }
        catch (Exception error)
        {
            cleanupFailures.Add(error);
        }

        if (windowsJob is not null && (ct.IsCancellationRequested || !processExitedDuringCleanup))
        {
            try { await windowsJob.TerminateAndWaitAsync(CancellationToken.None); }
            catch (Exception error)
            {
                cleanupFailures.Add(error);
                terminationSucceeded = TryKillProcessTree(process, cleanupFailures);
            }
        }
        else if (windowsJob is null && !processExitedDuringCleanup)
        {
            terminationSucceeded = TryKillProcessTree(process, cleanupFailures);
        }

        if (terminationSucceeded)
        {
            try { await process.WaitForExitAsync(CancellationToken.None); }
            catch (Exception error) { cleanupFailures.Add(error); }
        }
        else
        {
            // Do not wait without a bound on a process whose termination failed.
            cleanupFailures.Add(new InvalidOperationException(
                "Process termination failed; skipped an unbounded process wait."));
        }

        // Let the pumps drain through process termination first, matching the
        // original stream behavior. Stop any remaining blocked pump only after
        // the child has been terminated or the termination failure is recorded.
        TryCancel(pumpStop, cleanupFailures);
        var copiesIncomplete = (stdoutCopy.Task is not null && !stdoutCopy.Task.IsCompleted)
                               || (stderrCopy.Task is not null && !stderrCopy.Task.IsCompleted);
        if (copiesIncomplete)
        {
            TryCloseStandardOutput(process, cleanupFailures);
            TryCloseStandardError(process, cleanupFailures);
        }

        await ObservePumpForCleanupAsync(stdoutCopy, pumpStop, primaryFailure, observerFailures,
            cleanupFailures);
        await ObservePumpForCleanupAsync(stderrCopy, pumpStop, primaryFailure, observerFailures,
            cleanupFailures);

        if (copiesTask is not null && !copiesTaskObserved)
        {
            try { await copiesTask; }
            catch
            {
                // WhenAll is derived only from the two pumps already classified
                // above. Await it to observe its exception without reporting the
                // same member failures a second time.
            }
        }

        if (processWait is not null)
        {
            try { await processWait; }
            catch (OperationCanceledException) when (operationWaitStop?.IsCancellationRequested == true) { }
            catch (Exception error) { AddUniqueFailure(cleanupFailures, primaryFailure, observerFailures, error); }
        }

        if (windowsJob is not null)
        {
            try { windowsJob.Dispose(); }
            catch (Exception error) { cleanupFailures.Add(error); }
        }
        TryDispose(pumpStop, cleanupFailures);
        TryDispose(operationWaitStop, cleanupFailures);
        try { process.Dispose(); }
        catch (Exception error) { cleanupFailures.Add(error); }

        // The timer value preserves the old duration measurement boundary, but
        // callbacks run only after process, stream and supervision cleanup.
        if (heartbeatStop is not null)
        {
            try { await heartbeatTask; }
            catch (Exception error) { observerFailures.Add(error); }
        }
        if (durationEligible)
        {
            try { durationCompleted(elapsed); }
            catch (Exception error) { observerFailures.Add(error); }
        }

        TryDispose(heartbeatStop, cleanupFailures);

        ThrowFailures(primaryFailure, observerFailures, cleanupFailures);
        return outcome ?? throw new InvalidOperationException("Process execution produced no outcome.");
    }

    private static async Task PumpAsync(Stream source, Stream destination, CancellationToken stopToken,
        CopyState state, Func<bool> cleanupStarted)
    {
        try
        {
            if (!destination.CanWrite)
                throw new InvalidOperationException("The prepared process output stream is not writable.");
        }
        catch
        {
            state.FailurePhase = PumpPhase.Write;
            throw;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            while (true)
            {
                int count;
                try { count = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), stopToken); }
                catch
                {
                    state.FailurePhase = PumpPhase.Read;
                    state.FailureDuringInternalStop = stopToken.IsCancellationRequested;
                    state.FailureDuringCleanup = cleanupStarted();
                    throw;
                }
                if (count == 0) return;

                try { await destination.WriteAsync(buffer.AsMemory(0, count), stopToken); }
                catch
                {
                    state.FailurePhase = PumpPhase.Write;
                    state.FailureDuringInternalStop = stopToken.IsCancellationRequested;
                    throw;
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task ObservePumpForCleanupAsync(CopyState state,
        CancellationTokenSource? pumpStop, Exception? primary,
        List<Exception> observers, List<Exception> cleanupFailures)
    {
        if (state.Task is null) return;
        try { await state.Task; }
        catch (Exception error)
        {
            var stoppedInternally = pumpStop?.IsCancellationRequested == true;
            var expectedInternalTokenCancellation = stoppedInternally && IsPumpCancellation(error, pumpStop);
            var expectedReadTeardown = state.FailurePhase == PumpPhase.Read
                && (state.FailureDuringInternalStop || state.FailureDuringCleanup)
                && (error is IOException or ObjectDisposedException);
            if (expectedInternalTokenCancellation || expectedReadTeardown) return;
            AddUniqueFailure(cleanupFailures, primary, observers, error);
        }
    }

    private static bool IsPumpCancellation(Exception error, CancellationTokenSource? pumpStop) =>
        error is OperationCanceledException canceled
        && pumpStop is not null
        && canceled.CancellationToken == pumpStop.Token;

    private static bool TryKillProcessTree(Process process, List<Exception> failures)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return true;
        }
        catch (Exception error)
        {
            failures.Add(error);
            return false;
        }
    }

    private static void TryCancel(CancellationTokenSource? source, List<Exception> failures)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (Exception error) { failures.Add(error); }
    }

    private static void TryDispose(IDisposable? disposable, List<Exception> failures)
    {
        if (disposable is null) return;
        try { disposable.Dispose(); }
        catch (Exception error) { failures.Add(error); }
    }

    private static void TryCloseStandardOutput(Process process, List<Exception> failures)
    {
        try { process.StandardOutput.Close(); }
        catch (Exception error) { failures.Add(error); }
    }

    private static void TryCloseStandardError(Process process, List<Exception> failures)
    {
        try { process.StandardError.Close(); }
        catch (Exception error) { failures.Add(error); }
    }

    private static void AddUniqueFailure(List<Exception> target, Exception? primary,
        List<Exception> observers, Exception error)
    {
        if (ReferenceEquals(error, primary) || observers.Any(observer => ReferenceEquals(observer, error))
            || target.Any(existing => ReferenceEquals(existing, error))) return;
        target.Add(error);
    }

    private static void ThrowFailures(Exception? primary, List<Exception> observers,
        List<Exception> cleanup)
    {
        var failures = new List<Exception>();
        if (primary is not null) failures.Add(primary);
        failures.AddRange(observers);
        failures.AddRange(cleanup);
        if (failures.Count == 0) return;
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("Process execution and cleanup reported multiple failures.", failures);
    }

    private enum PumpPhase { Read, Write }

    private sealed class CopyState
    {
        public Task? Task { get; set; }
        public PumpPhase? FailurePhase { get; set; }
        public bool FailureDuringInternalStop { get; set; }
        public bool FailureDuringCleanup { get; set; }
    }
}
