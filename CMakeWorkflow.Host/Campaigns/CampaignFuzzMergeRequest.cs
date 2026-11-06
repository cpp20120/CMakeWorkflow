// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Campaigns;
#pragma warning restore IDE0130

public sealed record CampaignFuzzMergeRequest(
    string RunDirectory,
    string? OutputDirectory = null,
    bool Coverage = false);
