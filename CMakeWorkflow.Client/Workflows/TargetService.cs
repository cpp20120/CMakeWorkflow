namespace CMakeWorkflow.Client.Workflows;

/// <summary>Prepares Host target workflows without admitting execution; borrows the client lifetime.</summary>
public sealed class TargetService(CMakeWorkflowClient client)
{
    // After PrepareAsync use Workflows.PlanAsync and Workflows.StartAsync for durable execution.
    /// <summary>Persists a target workflow manifest for subsequent explicit preflight/start.</summary>
    /// <remarks>
    /// Host paths remain remote over SSH. Commit-once preparation may leave persisted files after cancellation/lost reply.
    /// Recover explicitly with the same preparation identity; no automatic reconnect, retry or execution occurs.
    /// </remarks>
    public Task<PreparedWorkflow> PrepareAsync(PrepareTargetOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("target/prepare");
        return client.Proxy.TargetPrepareAsync(options, ct);
    }
}
