namespace CMakeWorkflow.Host.Campaigns;

public sealed record CampaignExecutionResult(
    int SchemaVersion,
    string Kind,
    string Status,
    int ExitCode,
    string? ArtifactDirectory);
