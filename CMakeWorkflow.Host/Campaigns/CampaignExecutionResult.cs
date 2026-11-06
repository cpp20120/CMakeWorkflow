// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Campaigns;
#pragma warning restore IDE0130

public sealed record CampaignExecutionResult(
    int SchemaVersion,
    string Kind,
    string Status,
    int ExitCode,
    string? ArtifactDirectory);
