namespace CMakeWorkflow.Client.Campaigns;

public sealed record CampaignResult(
    int SchemaVersion,
    string Kind,
    string Status,
    int ExitCode,
    string? ArtifactDirectory);
