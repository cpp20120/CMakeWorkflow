// Preserve the established namespace; folders only group implementation.

namespace CMakeWorkflow.Core.Execution;
#pragma warning restore IDE0130

// The scheduler owns ready/active admission and bounded dispatch. A domain
// adapter owns authoritative state, durable commits and dispatch authorization.
/// <summary>Admits ready nodes with bounded parallelism using domain-owned state and hooks.</summary>
/// <remarks>
/// Requires a validated finite acyclic graph and positive jobs. Dependencies must succeed before admission.
/// The domain supplies conflict rules, durable running intents and optional dispatch authorization.
/// The scheduler does not serialize evidence or acquire domain leases. On interruption it signals stop and
/// awaits tracked workers; callers retain their lease/cleanup responsibilities if worker drain itself fails.
/// </remarks>
internal static class ExecutionScheduler
{
    /// <summary>Schedules validated nodes after committing every batch of running intents.</summary>
    /// <remarks>
    /// Invokes reservation/state callbacks serially; returned Execute tasks may overlap up to jobs. Persists the
    /// complete batch before optional ConfirmDispatch and any Execute call. A persistence/gate exception prevents
    /// that batch from launching, but reserved intent is not automatically rolled back.
    /// In finally, requests stop and awaits tracked tasks in sequence, then calls DrainInFlight. Exceptions from
    /// cancellation callbacks, tasks or drain hooks can interrupt this cleanup and propagate. Domain owners must
    /// retain their own resource/lease obligations and must not infer quiescence merely from an exception.
    /// </remarks>
    /// <param name="plan">Validated finite graph; borrowed definitions must remain stable throughout scheduling.</param>
    /// <param name="jobs">Positive maximum active/admitted workers; caller validates this precondition.</param>
    /// <param name="stop">Caller-owned worker stop source; canceled here even on normal completion, never disposed here.</param>
    /// <param name="hooks">Domain state, conflict, persistence and execution callbacks; no leases are acquired by the scheduler.</param>
    /// <param name="cancellation">Admission lifetime used for pending-node cancellation and callback checks; callers link stop as needed.</param>
    internal static async Task RunAsync<TPayload, TResult, TReservation>(
        ExecutionPlan<TPayload> plan, int jobs, CancellationTokenSource stop,
        ExecutionSchedulerHooks<TPayload, TResult, TReservation> hooks, CancellationToken cancellation)
    {
        List<ExecutionNode<TPayload>> remaining = [.. plan.Nodes.Where(n => hooks.GetStatus(n.Id) != ExecutionStatus.Succeeded)];
        Dictionary<string, (ExecutionNode<TPayload> Node, Task<TResult> Task)> active = new(StringComparer.Ordinal);
        try
        {
            while (remaining.Count > 0 || active.Count > 0)
            {
                if (plan.Nodes.Any(n => hooks.GetStatus(n.Id) == ExecutionStatus.NeedsAttention) && active.Count == 0)
                {
                    break;
                }

                foreach (ExecutionNode<TPayload>? node in remaining.ToArray())
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        hooks.MarkCanceled(node);
                        _ = remaining.Remove(node);
                    }
                    else if (node.Dependencies.Any(d => hooks.GetStatus(d) is
                                 ExecutionStatus.Failed or ExecutionStatus.Blocked or ExecutionStatus.Canceled))
                    {
                        hooks.MarkBlocked(node);
                        _ = remaining.Remove(node);
                    }
                }

                ExecutionNode<TPayload>[] ready = [.. remaining.Where(n => n.Dependencies.All(d =>
                    hooks.GetStatus(d) == ExecutionStatus.Succeeded))];
                ExecutionNode<TPayload>? exclusive = ready.FirstOrDefault(n => n.Exclusive);
                // A ready exclusive node drains active work before it is admitted.
                ExecutionNode<TPayload>[] candidates = exclusive is not null ? [exclusive] : ready;
                List<(ExecutionNode<TPayload> Node, TReservation Reservation)> dispatch = [];
                foreach (ExecutionNode<TPayload>? node in candidates)
                {
                    if (cancellation.IsCancellationRequested || active.Count + dispatch.Count >= jobs ||
                        plan.Nodes.Any(n => hooks.GetStatus(n.Id) == ExecutionStatus.NeedsAttention))
                    {
                        break;
                    }

                    if (active.Values.Any(a => hooks.Conflicts(node, a.Node)) ||
                        dispatch.Any(a => hooks.Conflicts(node, a.Node)))
                    {
                        continue;
                    }

                    await hooks.BeforeReserve(node, cancellation);
                    TReservation? reservation = hooks.Reserve(node);
                    _ = remaining.Remove(node);
                    dispatch.Add((node, reservation));
                }

                // The domain must commit the complete batch of 'running' intents
                // before authorization and before invoking *any* executor.
                await hooks.PersistCheckpoint();
                if (dispatch.Count != 0 && hooks.ConfirmDispatch is not null)
                {
                    await hooks.ConfirmDispatch(stop.Token);
                }

                foreach ((ExecutionNode<TPayload>? node, TReservation? reservation) in dispatch)
                {
                    await hooks.BeforeExecute(node, cancellation);
                    active.Add(node.Id, (node, hooks.Execute(node, reservation, stop.Token)));
                }

                if (active.Count == 0)
                {
                    if (plan.Nodes.Any(n => hooks.GetStatus(n.Id) == ExecutionStatus.NeedsAttention))
                    {
                        break;
                    }

                    if (remaining.Count > 0)
                    {
                        throw new InvalidOperationException("No schedulable workflow node.");
                    }

                    break;
                }

                _ = await Task.WhenAny(active.Values.Select(a => a.Task));
                foreach (KeyValuePair<string, (ExecutionNode<TPayload> Node, Task<TResult> Task)> pair in active.Where(p => p.Value.Task.IsCompleted).ToArray())
                {
                    TResult? result = await pair.Value.Task;
                    await hooks.AfterExecute(pair.Value.Node, cancellation);
                    hooks.Complete(pair.Value.Node, result);
                    _ = active.Remove(pair.Key);
                }

                await hooks.PersistCheckpoint();
            }
        }
        finally
        {
            // An interrupted scheduler must not release domain leases while
            // an admitted worker may still be producing external effects.
            await stop.CancelAsync();
            foreach (KeyValuePair<string, (ExecutionNode<TPayload> Node, Task<TResult> Task)> pair in active)
            {
                TResult? result = await pair.Value.Task;
                hooks.DrainInFlight(pair.Value.Node, result);
            }
        }
    }
}
