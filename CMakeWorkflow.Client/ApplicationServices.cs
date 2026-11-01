namespace CMakeWorkflow.Client;

// One typed request per capability. Neither SDK nor Host wraps an arbitrary CLI argv.
public sealed class ProfileService(CMakeWorkflowClient client)
{
    public Task<PreparedWorkflow> PrepareAsync(PrepareProfileOptions options, CancellationToken ct = default)
    { client.RequireCapability("profile/prepare"); return client.Proxy.ProfilePrepareAsync(options, ct); }
    public Task<AnalyzedProfile> AnalyzeAsync(AnalyzeProfileOptions options, CancellationToken ct = default)
    { client.RequireCapability("profile/analyze"); return client.Proxy.ProfileAnalyzeAsync(options, ct); }
    /// <summary>Request-scoped; not a durable operation. Never retry on an ambiguous disconnect.</summary>
    public Task<ReplayProfileResult> ReplayAsync(ReplayProfileOptions options, CancellationToken ct = default)
    { client.RequireCapability("profile/replay"); return client.Proxy.ProfileReplayAsync(options, ct); }
}

public sealed class TargetService(CMakeWorkflowClient client)
{
    // After PrepareAsync use Workflows.PlanAsync and Workflows.StartAsync for durable execution.
    public Task<PreparedWorkflow> PrepareAsync(PrepareTargetOptions options, CancellationToken ct = default)
    { client.RequireCapability("target/prepare"); return client.Proxy.TargetPrepareAsync(options, ct); }
}

public sealed class CampaignService(CMakeWorkflowClient client)
{
    /// <summary>Connection-scoped; cancel/disconnect can leave partial artifacts.</summary>
    public Task<CampaignResult> MutationAsync(MutationCampaignOptions options, CancellationToken ct = default)
    { client.RequireCapability("dagflow/mutation/run"); return client.Proxy.CampaignMutationAsync(options, ct); }
    public Task<CampaignResult> WeakMemoryAsync(WeakMemoryCampaignOptions options, CancellationToken ct = default)
    { client.RequireCapability("dagflow/weakMemory/run"); return client.Proxy.CampaignWeakMemoryAsync(options, ct); }
    public Task<CampaignResult> FuzzSeedAsync(FuzzSeedCampaignOptions options, CancellationToken ct = default)
    { client.RequireCapability("dagflow/fuzz/seed"); return client.Proxy.CampaignFuzzSeedAsync(options, ct); }
    public Task<CampaignResult> FuzzRunAsync(FuzzRunCampaignOptions options, CancellationToken ct = default)
    { client.RequireCapability("dagflow/fuzz/run"); return client.Proxy.CampaignFuzzRunAsync(options, ct); }
    public Task<CampaignResult> FuzzTriageAsync(FuzzTriageCampaignOptions options, CancellationToken ct = default)
    { client.RequireCapability("dagflow/fuzz/triage"); return client.Proxy.CampaignFuzzTriageAsync(options, ct); }
    public Task<CampaignResult> FuzzMergeAsync(FuzzMergeCampaignOptions options, CancellationToken ct = default)
    { client.RequireCapability("dagflow/fuzz/merge"); return client.Proxy.CampaignFuzzMergeAsync(options, ct); }
}

public sealed class HistoryService(CMakeWorkflowClient client)
{
    public Task<ImportHistoryResult> ImportAsync(ImportHistoryOptions options, CancellationToken ct = default)
    { client.RequireCapability("history/import"); return client.Proxy.HistoryImportAsync(options, ct); }
}
