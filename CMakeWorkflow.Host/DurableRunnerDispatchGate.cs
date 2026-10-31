namespace CMakeWorkflow.Host;

// Opt-in bridge for a future host-owned workflow/start implementation.
// The caller must hold the runner lease; this gate does not acquire it,
// schedule work, or change the acceptance state.
public sealed class DurableRunnerDispatchGate(string workspace, string operationId)
{
    public Task ConfirmAsync(string runDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = new DurableAcceptanceStore(workspace);
        store.ConfirmRunnerCheckpoint(operationId, runDirectory);
        return Task.CompletedTask;
    }
}
