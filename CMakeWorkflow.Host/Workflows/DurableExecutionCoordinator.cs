using System.Collections.Concurrent;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

/// <summary>
/// Host-owned execution. In-memory entries suppress concurrent dispatch, while
/// SQLite acceptance is authoritative across connections and process restarts.
/// </summary>
public sealed class DurableExecutionCoordinator(CancellationToken shutdown) : IDisposable
{
    private readonly CancellationToken shutdown = shutdown;
    private readonly ConcurrentDictionary<string, Task> active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> cancellation = new(StringComparer.Ordinal);
    private int disposed;

    public OperationReference Start(string workspace, WorkflowStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref disposed) != 0 || shutdown.IsCancellationRequested)
        {
            throw new InvalidOperationException("host_shutting_down");
        }

        using DurableAcceptanceStore store = new(workspace);
        AcceptedWorkflow accepted = store.Accept(request.ClientRequestId, request.Manifest,
            request.ManifestSha256, request.Jobs, request.ResultsDirectory);
        OperationReference reference = new(accepted.WorkspaceId, accepted.OperationId,
            accepted.ClientRequestId, "workflow", accepted.State,
            accepted.ManifestSha256, accepted.ManifestBaseDirectory);
        if (accepted.State != "accepted_not_dispatched")
        {
            return reference;
        }

        // An accepted operation may be scheduled by two independent connections.
        // The coordinator owns the task; the connection's cancellation token is not used.
        // Executor's cross-process file lease provides a second admission boundary.
        string key = accepted.WorkspaceId + ":" + accepted.OperationId;
        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource tokenSource = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        if (!active.TryAdd(key, tcs.Task))
        {
            tokenSource.Dispose();
            return reference;
        }

        cancellation[key] = tokenSource;
        // A cancellation may have committed after Accept but before task registration.
        if (store.Get(accepted.OperationId)?.State == "cancel_requested")
        {
            tokenSource.Cancel();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                _ = await DurableWorkflowExecutor.ExecuteAcceptedAsync(workspace, accepted.OperationId,
                    request.ClientRequestId, request.Manifest, request.ManifestSha256,
                    request.Jobs, request.ResultsDirectory, tokenSource.Token);
                _ = tcs.TrySetResult();
            }
            catch (OperationCanceledException) when (tokenSource.IsCancellationRequested)
            {
                _ = tcs.TrySetCanceled(tokenSource.Token);
            }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
            catch (Exception error)
            {
                await Console.Error.WriteLineAsync($"[Host workflow:{accepted.OperationId}] {error}");
                _ = tcs.TrySetException(error);
                // The task is observed below, so a background failure cannot crash the Host.
            }
#pragma warning restore CA1031

            finally
            {
                _ = active.TryRemove(key, out _);
                _ = cancellation.TryRemove(key, out _);
                tokenSource.Dispose();
            }
        });
        _ = tcs.Task.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return reference;
    }

    // A non-CMake domain shares durable requestId, cancellation, leases and
    // lost-reply recovery, but executes its own immutable snapshot and report.
    public OperationReference StartProcess(string workspace, ExecutionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref disposed) != 0 || shutdown.IsCancellationRequested)
        {
            throw new InvalidOperationException("host_shutting_down");
        }
        // Reject oversized process manifests before SQLite snapshots their bytes.
        // Domain-specific resource limits must not alter legacy CMake admission.
        WorkspaceReadPaths paths = new(workspace);
        string manifestPath = paths.Require(request.Manifest);
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("Process execution manifest is missing or exceeds 2 MiB.");
        }

        using DurableAcceptanceStore store = new(workspace, false, ProcessCheckpointEvidenceCodec.Instance);
        DurableImmutableRequest immutable = ProcessWorkflowDomain.Admission(request);
        AcceptedWorkflow accepted = store.AcceptImmutable(immutable);
        OperationReference reference = new(accepted.WorkspaceId, accepted.OperationId,
            accepted.ClientRequestId, "process", accepted.State,
            accepted.ManifestSha256, accepted.ManifestBaseDirectory);
        if (accepted.State != "accepted_not_dispatched")
        {
            return reference;
        }

        string key = accepted.WorkspaceId + ":" + accepted.OperationId;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        if (!active.TryAdd(key, completion.Task))
        {
            linked.Dispose();
            return reference;
        }
        cancellation[key] = linked;
        if (store.Get(accepted.OperationId)?.State == "cancel_requested")
        {
            linked.Cancel();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                _ = await DurableProcessWorkflowExecutor.ExecuteAcceptedAsync(workspace, accepted.OperationId,
                    immutable, request.Jobs, linked.Token);
                _ = completion.TrySetResult();
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                _ = completion.TrySetCanceled(linked.Token);
            }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
            catch (Exception error)
            {
                await Console.Error.WriteLineAsync($"[Host process:{accepted.OperationId}] {error}");
                _ = completion.TrySetException(error);
            }
#pragma warning restore CA1031

            finally
            {
                _ = active.TryRemove(key, out _);
                _ = cancellation.TryRemove(key, out _);
                linked.Dispose();
            }
        });
        _ = completion.Task.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return reference;
    }

    public void Resume(string workspace, string operationId)
    {
        if (Volatile.Read(ref disposed) != 0 || shutdown.IsCancellationRequested)
        {
            throw new InvalidOperationException("host_shutting_down");
        }

        using DurableAcceptanceStore store = new(workspace);
        DurableAcceptanceStore.RecoveryInspection inspection = store.InspectRecovery(operationId);
        string domain = store.GetDomainKind(operationId);
        if (domain is not ("workflow" or "process"))
        {
            throw new InvalidOperationException("resume_unsupported_for_execution_domain");
        }

        if (inspection.Revision != 2 || inspection.Decision != "needs_attention" ||
            inspection.TerminalState is not null || inspection.CancellationRequested)
        {
            throw new InvalidOperationException("resume_not_admitted_or_bound");
        }

        string key = store.WorkspaceId + ":" + operationId;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        if (!active.TryAdd(key, completion.Task))
        {
            // A second client may retry while the first execution is active.
            linked.Dispose();
            return;
        }

        cancellation[key] = linked;
        if (store.InspectRecovery(operationId).CancellationRequested)
        {
            linked.Cancel();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                _ = await HostOperationComposition.ResumeAsync(domain, workspace, operationId, linked.Token);
                _ = completion.TrySetResult();
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                _ = completion.TrySetCanceled(linked.Token);
            }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
            catch (Exception error)
            {
                await Console.Error.WriteLineAsync($"[Host resume:{operationId}] {error}");
                _ = completion.TrySetException(error);
            }
#pragma warning restore CA1031

            finally
            {
                _ = active.TryRemove(key, out _);
                _ = cancellation.TryRemove(key, out _);
                linked.Dispose();
            }
        });
        _ = completion.Task.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public DurableAcceptanceStore.CancellationDecision Cancel(string workspace, string operationId,
        long expectedRevision)
    {
        if (Volatile.Read(ref disposed) != 0 || shutdown.IsCancellationRequested)
        {
            throw new InvalidOperationException("host_shutting_down");
        }

        using DurableAcceptanceStore store = new(workspace);
        DurableAcceptanceStore.CancellationDecision decision = store.RequestCancellation(operationId, expectedRevision);
        if (decision.Requested && cancellation.TryGetValue(store.WorkspaceId + ":" + operationId, out CancellationTokenSource? token))
        {
            try
            {
                token.Cancel();
            }
            catch (ObjectDisposedException)
            {
                /* Executor completed while cancellation was committing. */
            }
        }

        return decision;
    }

    public bool IsActive(string workspaceId, string operationId) =>
        active.ContainsKey(workspaceId + ":" + operationId);

    public async Task DrainAsync()
    {
        while (!active.IsEmpty)
        {
            await Task.WhenAll(active.Values.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);
}
