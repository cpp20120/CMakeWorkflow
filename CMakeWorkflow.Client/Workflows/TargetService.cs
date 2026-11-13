using CMakeWorkflow.Client.Protocol;

namespace CMakeWorkflow.Client.Workflows;

/// <summary>Reads CMake target metadata and prepares Host target workflows; borrows the client lifetime.</summary>
public sealed class TargetService(CMakeWorkflowClient client)
{
    /// <summary>Reads CMake metadata from a Host build directory without configuring it.</summary>
    public Task<BuildContextObservation> InspectContextAsync(string buildDirectory, CancellationToken ct = default)
    {
        client.RequireCapability("context/inspect");
        return RpcRefusals.TranslateAsync(() => client.Proxy.InspectContextAsync(new(buildDirectory), ct));
    }

    /// <summary>Resolves an executable through the Host CMake File API without building it.</summary>
    public Task<TargetResolution> ResolveAsync(string buildDirectory, string target,
        string? configuration = null, CancellationToken ct = default)
    {
        client.RequireCapability("target/resolve");
        return RpcRefusals.TranslateAsync(() => client.Proxy.ResolveTargetAsync(new(buildDirectory, target, configuration), ct));
    }

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
