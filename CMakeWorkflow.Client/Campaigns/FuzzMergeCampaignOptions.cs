namespace CMakeWorkflow.Client.Campaigns;

public sealed record FuzzMergeCampaignOptions(
    string RunDirectory,
    string? OutputDirectory = null,
    bool Coverage = false);
