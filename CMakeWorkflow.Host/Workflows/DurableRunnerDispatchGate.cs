namespace CMakeWorkflow.Host.Workflows;

/// <summary>Binds a runner checkpoint immediately before external dispatch.</summary>
/// <remarks>
/// The caller must hold the runner lease and have durably persisted running intent.
/// This gate acquires no execution lease and neither schedules work nor changes acceptance authorization.
/// </remarks>
public sealed class DurableRunnerDispatchGate(string workspace, string operationId)
{
    /// <summary>Verifies and commits the operation/checkpoint binding before any worker launch.</summary>
    /// <remarks>
    /// Failure prevents the dispatch batch. Successful return proves checkpoint binding, not external process start.
    /// </remarks>
    /// <param name="runDirectory">Authorized Host run directory containing the durable checkpoint.</param>
    /// <param name="cancellationToken">Coordinator execution token; checked before binding, not an individual RPC token.</param>
    public Task ConfirmAsync(string runDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using DurableAcceptanceStore store = new(workspace);
        _ = store.ConfirmRunnerCheckpoint(operationId, runDirectory);
        return Task.CompletedTask;
    }
}