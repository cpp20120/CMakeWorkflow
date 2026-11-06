namespace CMakeWorkflow.Client.Campaigns;

/// <summary>Runs request/connection-scoped Host campaigns; borrows the client lifetime.</summary>
/// <remarks>
/// Campaigns can execute tools and write corpus/report artifacts. Cancellation/disconnect can leave partial effects.
/// These methods do not return durable operation handles, promise idempotency or automatically retry/reconnect.
/// All paths are interpreted on the initialized Host.
/// </remarks>
public sealed class CampaignService(CMakeWorkflowClient client)
{
    /// <summary>Runs a mutation campaign.</summary>
    /// <remarks>
    /// Cancellation is cooperative request cancellation and can follow external effects.
    /// Inspect Host artifacts after an uncertain reply before deciding whether another invocation is appropriate.
    /// </remarks>
    public Task<CampaignResult> MutationAsync(MutationCampaignOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("dagflow/mutation/run");
        return client.Proxy.CampaignMutationAsync(options, ct);
    }

    /// <summary>Runs a weak-memory validation campaign.</summary>
    /// <remarks>
    /// Cancellation is cooperative request cancellation and can follow external effects.
    /// Inspect Host artifacts after an uncertain reply before deciding whether another invocation is appropriate.
    /// </remarks>
    public Task<CampaignResult> WeakMemoryAsync(WeakMemoryCampaignOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("dagflow/weakMemory/run");
        return client.Proxy.CampaignWeakMemoryAsync(options, ct);
    }

    /// <summary>Generates fuzz corpus seeds.</summary>
    /// <remarks>
    /// Cancellation is cooperative request cancellation and can follow external effects.
    /// Inspect Host artifacts after an uncertain reply before deciding whether another invocation is appropriate.
    /// </remarks>
    public Task<CampaignResult> FuzzSeedAsync(FuzzSeedCampaignOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("dagflow/fuzz/seed");
        return client.Proxy.CampaignFuzzSeedAsync(options, ct);
    }

    /// <summary>Runs a fuzz campaign.</summary>
    /// <remarks>
    /// Cancellation is cooperative request cancellation and can follow external effects.
    /// Inspect Host artifacts after an uncertain reply before deciding whether another invocation is appropriate.
    /// </remarks>
    public Task<CampaignResult> FuzzRunAsync(FuzzRunCampaignOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("dagflow/fuzz/run");
        return client.Proxy.CampaignFuzzRunAsync(options, ct);
    }

    /// <summary>Triages fuzz evidence.</summary>
    /// <remarks>
    /// Cancellation is cooperative request cancellation and can follow external effects.
    /// Inspect Host artifacts after an uncertain reply before deciding whether another invocation is appropriate.
    /// </remarks>
    public Task<CampaignResult> FuzzTriageAsync(FuzzTriageCampaignOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("dagflow/fuzz/triage");
        return client.Proxy.CampaignFuzzTriageAsync(options, ct);
    }

    /// <summary>Merges fuzz corpus evidence.</summary>
    /// <remarks>
    /// Cancellation is cooperative request cancellation and can follow external effects.
    /// Inspect Host artifacts after an uncertain reply before deciding whether another invocation is appropriate.
    /// </remarks>
    public Task<CampaignResult> FuzzMergeAsync(FuzzMergeCampaignOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("dagflow/fuzz/merge");
        return client.Proxy.CampaignFuzzMergeAsync(options, ct);
    }
}
