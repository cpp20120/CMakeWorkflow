namespace CMakeWorkflow.Client;

// One typed request per capability. Neither SDK nor Host wraps an arbitrary CLI argv.
public sealed class ProfileService(CMakeWorkflowClient client)
{
    public Task<PreparedWorkflow> PrepareAsync(PrepareProfileOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("profile/prepare");
        return client.Proxy.ProfilePrepareAsync(options, ct);
    }

    public Task<AnalyzedProfile> AnalyzeAsync(AnalyzeProfileOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("profile/analyze");
        return client.Proxy.ProfileAnalyzeAsync(options, ct);
    }

    /// <summary>Request-scoped; not a durable operation. Never retry on an ambiguous disconnect.</summary>
    public Task<ReplayProfileResult> ReplayAsync(ReplayProfileOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("profile/replay");
        return client.Proxy.ProfileReplayAsync(options, ct);
    }
}
