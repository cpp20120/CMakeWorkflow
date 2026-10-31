using System.Collections.Concurrent;

namespace CMakeWorkflow.Host;

/// <summary>
/// Host-owned execution. In-memory entries suppress concurrent dispatch, while
/// SQLite acceptance is authoritative across connections and process restarts.
/// </summary>
public sealed class DurableExecutionCoordinator : IDisposable
{
    private readonly CancellationToken shutdown;
    private readonly ConcurrentDictionary<string, Task> active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> cancellation = new(StringComparer.Ordinal);
    private int disposed;

    public DurableExecutionCoordinator(CancellationToken shutdown) => this.shutdown = shutdown;

    public OperationReference Start(string workspace, WorkflowStartRequest request)
    {
        if (Volatile.Read(ref disposed) != 0 || shutdown.IsCancellationRequested)
            throw new InvalidOperationException("host_shutting_down");
        using var store = new DurableAcceptanceStore(workspace);
        var accepted = store.Accept(request.ClientRequestId, request.Manifest,
            request.ManifestSha256, request.Jobs, request.ResultsDirectory);
        var reference = new OperationReference(accepted.WorkspaceId, accepted.OperationId,
            accepted.ClientRequestId, "workflow", accepted.State,
            accepted.ManifestSha256, accepted.ManifestBaseDirectory);
        if (accepted.State != "accepted_not_dispatched") return reference;

        // An accepted operation may be scheduled by two independent connections.
        // The coordinator owns the task; the connection's cancellation token is not used.
        // Executor's cross-process file lease provides a second admission boundary.
        var key = accepted.WorkspaceId + ":" + accepted.OperationId;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        if (!active.TryAdd(key, tcs.Task)) { tokenSource.Dispose(); return reference; }
        cancellation[key] = tokenSource;
        // A cancellation may have committed after Accept but before task registration.
        if (store.Get(accepted.OperationId)?.State == "cancel_requested") tokenSource.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, accepted.OperationId,
                    request.ClientRequestId, request.Manifest, request.ManifestSha256,
                    request.Jobs, request.ResultsDirectory, tokenSource.Token);
                tcs.TrySetResult();
            }
            catch (OperationCanceledException) when (tokenSource.IsCancellationRequested)
            {
                tcs.TrySetCanceled(tokenSource.Token);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"[Host workflow:{accepted.OperationId}] {error}");
                tcs.TrySetException(error);
                // The task is observed below, so a background failure cannot crash the Host.
            }
            finally { active.TryRemove(key, out _); cancellation.TryRemove(key, out _); tokenSource.Dispose(); }
        });
        _ = tcs.Task.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return reference;
    }

    public void Resume(string workspace, string operationId)
    {
        if (Volatile.Read(ref disposed) != 0 || shutdown.IsCancellationRequested)
            throw new InvalidOperationException("host_shutting_down");
        using var store = new DurableAcceptanceStore(workspace);
        var inspection = store.InspectRecovery(operationId);
        if (inspection.Revision != 2 || inspection.Decision != "needs_attention" ||
            inspection.TerminalState is not null || inspection.CancellationRequested)
            throw new InvalidOperationException("resume_not_admitted_or_bound");
        var key = store.WorkspaceId + ":" + operationId;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        if (!active.TryAdd(key, completion.Task))
        {
            // A second client may retry while the first execution is active.
            linked.Dispose();
            return;
        }
        cancellation[key] = linked;
        _ = Task.Run(async () =>
        {
            try
            {
                await DurableWorkflowExecutor.ExecuteResumedAsync(workspace, operationId, linked.Token);
                completion.TrySetResult();
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            { completion.TrySetCanceled(linked.Token); }
            catch (Exception error)
            {
                Console.Error.WriteLine($"[Host resume:{operationId}] {error}");
                completion.TrySetException(error);
            }
            finally
            {
                active.TryRemove(key, out _);
                cancellation.TryRemove(key, out _);
                linked.Dispose();
            }
        });
        _ = completion.Task.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public DurableAcceptanceStore.CancellationDecision Cancel(string workspace, string operationId, long expectedRevision)
    {
        if (Volatile.Read(ref disposed) != 0 || shutdown.IsCancellationRequested)
            throw new InvalidOperationException("host_shutting_down");
        using var store = new DurableAcceptanceStore(workspace);
        var decision = store.RequestCancellation(operationId, expectedRevision);
        if (decision.Requested && cancellation.TryGetValue(store.WorkspaceId + ":" + operationId, out var token))
        {
            try { token.Cancel(); }
            catch (ObjectDisposedException) { /* Executor completed while cancellation was committing. */ }
        }
        return decision;
    }

    public bool IsActive(string workspaceId, string operationId) =>
        active.ContainsKey(workspaceId + ":" + operationId);

    public async Task DrainAsync()
    {
        while (active.Count != 0)
            await Task.WhenAll(active.Values.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
    }

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);
}
