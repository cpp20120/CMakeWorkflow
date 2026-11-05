namespace CMakeWorkflow.Client.Campaigns;

public sealed class CampaignService(CMakeWorkflowClient client)
{
    /// <summary>Connection-scoped; cancel/disconnect can leave partial artifacts.</summary>
    public Task<CampaignResult> MutationAsync(MutationCampaignOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("dagflow/mutation/run");
        return client.Proxy.CampaignMutationAsync(options, ct);
    }

    public Task<CampaignResult> WeakMemoryAsync(WeakMemoryCampaignOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("dagflow/weakMemory/run");
        return client.Proxy.CampaignWeakMemoryAsync(options, ct);
    }

    public Task<CampaignResult> FuzzSeedAsync(FuzzSeedCampaignOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("dagflow/fuzz/seed");
        return client.Proxy.CampaignFuzzSeedAsync(options, ct);
    }

    public Task<CampaignResult> FuzzRunAsync(FuzzRunCampaignOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("dagflow/fuzz/run");
        return client.Proxy.CampaignFuzzRunAsync(options, ct);
    }

    public Task<CampaignResult> FuzzTriageAsync(FuzzTriageCampaignOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("dagflow/fuzz/triage");
        return client.Proxy.CampaignFuzzTriageAsync(options, ct);
    }

    public Task<CampaignResult> FuzzMergeAsync(FuzzMergeCampaignOptions options, CancellationToken ct = default)
    {
        client.RequireCapability("dagflow/fuzz/merge");
        return client.Proxy.CampaignFuzzMergeAsync(options, ct);
    }
}
