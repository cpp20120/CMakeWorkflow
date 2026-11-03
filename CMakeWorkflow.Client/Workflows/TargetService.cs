namespace CMakeWorkflow.Client;

public sealed class TargetService(CMakeWorkflowClient client)
{
    // After PrepareAsync use Workflows.PlanAsync and Workflows.StartAsync for durable execution.
    public Task<PreparedWorkflow> PrepareAsync(PrepareTargetOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("target/prepare");
        return client.Proxy.TargetPrepareAsync(options, ct);
    }
}
