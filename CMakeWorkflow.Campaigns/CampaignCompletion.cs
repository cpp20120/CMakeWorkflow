namespace CMakeWorkflow.Campaigns;

internal sealed record CampaignCompletion(
    int SchemaVersion,
    string Campaign,
    string Report,
    string ReportSha256,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Status);
