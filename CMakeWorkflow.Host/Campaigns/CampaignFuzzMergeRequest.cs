namespace CMakeWorkflow.Host.Campaigns;

public sealed record CampaignFuzzMergeRequest(
    string RunDirectory,
    string? OutputDirectory = null,
    bool Coverage = false);
