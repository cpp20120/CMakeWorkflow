namespace CMakeWorkflow.Engine;

// The scheduler owns ready/active admission and bounded dispatch. A domain
// adapter owns authoritative state, durable commits and dispatch authorization.
internal static class ExecutionScheduler
{
    internal static async Task RunAsync<TPayload, TResult, TReservation>(
        ExecutionPlan<TPayload> plan, int jobs, CancellationToken cancellation,
        CancellationTokenSource stop,
        ExecutionSchedulerHooks<TPayload, TResult, TReservation> hooks)
    {
        var remaining = plan.Nodes.Where(n => hooks.GetStatus(n.Id) != ExecutionStatus.Succeeded).ToList();
        var active = new Dictionary<string, (ExecutionNode<TPayload> Node, Task<TResult> Task)>(StringComparer.Ordinal);
        try
        {
            while (remaining.Count > 0 || active.Count > 0)
            {
                if (plan.Nodes.Any(n => hooks.GetStatus(n.Id) == ExecutionStatus.NeedsAttention) && active.Count == 0)
                    break;
                foreach (var node in remaining.ToArray())
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        hooks.MarkCanceled(node);
                        remaining.Remove(node);
                    }
                    else if (node.Dependencies.Any(d => hooks.GetStatus(d) is
                                 ExecutionStatus.Failed or ExecutionStatus.Blocked or ExecutionStatus.Canceled))
                    {
                        hooks.MarkBlocked(node);
                        remaining.Remove(node);
                    }
                }

                var ready = remaining.Where(n => n.Dependencies.All(d =>
                    hooks.GetStatus(d) == ExecutionStatus.Succeeded)).ToArray();
                var exclusive = ready.FirstOrDefault(n => n.Exclusive);
                // A ready exclusive node drains active work before it is admitted.
                var candidates = exclusive is not null ? new[] { exclusive } : ready;
                var dispatch = new List<(ExecutionNode<TPayload> Node, TReservation Reservation)>();
                foreach (var node in candidates)
                {
                    if (cancellation.IsCancellationRequested || active.Count + dispatch.Count >= jobs ||
                        plan.Nodes.Any(n => hooks.GetStatus(n.Id) == ExecutionStatus.NeedsAttention)) break;
                    if (active.Values.Any(a => hooks.Conflicts(node, a.Node)) ||
                        dispatch.Any(a => hooks.Conflicts(node, a.Node))) continue;
                    await hooks.BeforeReserve(node, cancellation);
                    var reservation = hooks.Reserve(node);
                    remaining.Remove(node);
                    dispatch.Add((node, reservation));
                }

                // The domain must commit the complete batch of 'running' intents
                // before authorization and before invoking *any* executor.
                await hooks.PersistCheckpoint();
                if (dispatch.Count != 0 && hooks.ConfirmDispatch is not null)
                    await hooks.ConfirmDispatch(stop.Token);
                foreach (var (node, reservation) in dispatch)
                {
                    await hooks.BeforeExecute(node, cancellation);
                    active.Add(node.Id, (node, hooks.Execute(node, reservation, stop.Token)));
                }

                if (active.Count == 0)
                {
                    if (plan.Nodes.Any(n => hooks.GetStatus(n.Id) == ExecutionStatus.NeedsAttention)) break;
                    if (remaining.Count > 0)
                        throw new InvalidOperationException("No schedulable workflow node.");
                    break;
                }

                await Task.WhenAny(active.Values.Select(a => a.Task));
                foreach (var pair in active.Where(p => p.Value.Task.IsCompleted).ToArray())
                {
                    var result = await pair.Value.Task;
                    await hooks.AfterExecute(pair.Value.Node, cancellation);
                    hooks.Complete(pair.Value.Node, result);
                    active.Remove(pair.Key);
                }

                await hooks.PersistCheckpoint();
            }
        }
        finally
        {
            // An interrupted scheduler must not release domain leases while
            // an admitted worker may still be producing external effects.
            await stop.CancelAsync();
            foreach (var pair in active)
            {
                var result = await pair.Value.Task;
                hooks.DrainInFlight(pair.Value.Node, result);
            }
        }
    }
}
