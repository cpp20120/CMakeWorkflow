using System.Collections.Concurrent;
using CMakeWorkflow.Host.Execution;
using CMakeWorkflow.Host.Sessions;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

/// <summary>Owns background execution tasks independently of individual RPC connections.</summary>
/// <remarks>
/// Accepted work is linked to the supplied Host shutdown token, never the submitting request token.
/// The in-memory task map prevents duplicate scheduling here; executor file leases and ledger transitions guard cross-process execution.
/// The Host owner signals shutdown and drains tasks before disposing shared resources. Background failures are observed
/// and logged; they do not manufacture terminal success/failure evidence for an uncertain runner.
/// </remarks>
public sealed class DurableExecutionCoordinator(CancellationToken shutdown) : IDisposable
{
    private readonly CancellationToken shutdown = shutdown;
    private readonly ConcurrentDictionary<string, Task> active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> cancellation = new(StringComparer.Ordinal);
    private int disposed;

    /// <summary>Accepts a CMake request durably and schedules eligible work under Host ownership.</summary>
    /// <remarks>
    /// A returned reference acknowledges acceptance, not execution completion. Existing non-initial state is returned without implicit redispatch.
    /// </remarks>
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
    /// <summary>Accepts a process-domain request and schedules it under Host ownership.</summary>
    /// <remarks>
    /// Domain adapters validate immutable input; admission and lost-reply semantics match durable CMake execution.
    /// </remarks>
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

    /// <summary>Schedules an admitted recovery attempt without borrowing the caller RPC lifetime.</summary>
    /// <remarks>
    /// Requires recovery revision 2, needs_attention, no cancellation and no finalization. The executor owns the permanent one-shot claim.
    /// </remarks>
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

    /// <summary>Persists cancellation intent before signaling any registered worker token.</summary>
    /// <remarks>
    /// An executor may complete concurrently. A returned receipt is not proof of worker shutdown; terminal evidence is read separately.
    /// </remarks>
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

    /// <summary>Waits for registered execution tasks to leave the active map.</summary>
    /// <remarks>
    /// Does not initiate cancellation. The Host owner must signal its shutdown token before shutdown drain.
    /// </remarks>
    public async Task DrainAsync()
    {
        while (!active.IsEmpty)
        {
            await Task.WhenAll(active.Values.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    /// <summary>Prevents new coordinator mutations.</summary>
    /// <remarks>
    /// Does not cancel or drain active tasks. The Host owner supplies shutdown cancellation and awaits DrainAsync first.
    /// </remarks>
    public void Dispose() => Interlocked.Exchange(ref disposed, 1);
}
