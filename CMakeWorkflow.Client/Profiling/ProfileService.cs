using CMakeWorkflow.Client.Workflows;

namespace CMakeWorkflow.Client.Profiling;

// One typed request per capability. Neither SDK nor Host wraps an arbitrary CLI argv.
/// <summary>Host profile preparation, analysis and request-scoped replay; borrows the client lifetime.</summary>
/// <remarks>
/// Paths are interpreted on the Host. Calls propagate cancellation and transport failures without automatic retry.
/// Replay executes work under request/connection lifetime, unlike durable Workflows.StartAsync.
/// </remarks>
public sealed class ProfileService(CMakeWorkflowClient client)
{
    /// <summary>Prepares a Host profile workflow without launching its build or capture nodes.</summary>
    /// <remarks>
    /// Preparation may persist files before RPC cancellation/loss of reply; recover with the same request identity.
    /// Use explicit preflight and durable start to execute the prepared manifest.
    /// </remarks>
    public Task<PreparedWorkflow> PrepareAsync(PrepareProfileOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("profile/prepare");
        return client.Proxy.ProfilePrepareAsync(options, ct);
    }

    /// <summary>Analyzes existing Host profile evidence and returns generated analysis artifacts.</summary>
    /// <remarks>
    /// May write analysis outputs. Cancellation can leave partial outputs; no automatic retry or rollback is provided.
    /// </remarks>
    public Task<AnalyzedProfile> AnalyzeAsync(AnalyzeProfileOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("profile/analyze");
        return client.Proxy.ProfileAnalyzeAsync(options, ct);
    }

    /// <summary>Prepares a durable replay DAG for explicit workflow/start admission.</summary>
    /// <remarks>
    /// Preparation is commit-once, not execution. Use Workflows.StartAsync with the
    /// returned manifest/hash and recover the operation by its stable client request ID.
    /// Completed capture nodes survive reconnect; interrupted captures require reconciliation.
    /// Analyze the completed run explicitly with AnalyzeAsync.
    /// </remarks>
    public Task<PreparedWorkflow> PrepareReplayAsync(PrepareReplayProfileOptions options,
        CancellationToken ct = default)
    {
        client.RequireCapability("profileReplay/prepare");
        return client.Proxy.ProfileReplayPrepareAsync(options, ct);
    }

    /// <summary>Executes a profile replay under request/connection lifetime.</summary>
    /// <remarks>
    /// This is not durable acceptance. Cancellation/disconnect can leave external effects or partial artifacts.
    /// A lost reply does not prove replay did not run; do not blindly retry an ambiguous outcome.
    /// </remarks>
    public Task<ReplayProfileResult> ReplayAsync(ReplayProfileOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("profile/replay");
        return client.Proxy.ProfileReplayAsync(options, ct);
    }
}
