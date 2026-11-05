namespace CMakeWorkflow.Client.Campaigns;

public sealed record FuzzRunCampaignOptions(
    string BuildDirectory,
    string Layer,
    string ResultsDirectory,
    string? Backend = null,
    string? CorpusDirectory = null,
    string? Configuration = null,
    int DurationSeconds = 60,
    int InputTimeoutSeconds = 15,
    int MaxLength = 4096,
    bool ReplayOnly = false);
