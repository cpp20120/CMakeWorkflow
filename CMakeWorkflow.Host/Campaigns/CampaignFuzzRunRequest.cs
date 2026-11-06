// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Campaigns;
#pragma warning restore IDE0130

public sealed record CampaignFuzzRunRequest(
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
