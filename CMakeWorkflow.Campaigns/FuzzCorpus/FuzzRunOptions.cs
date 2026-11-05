namespace CMakeWorkflow.Campaigns.FuzzCorpus;

public sealed record FuzzRunOptions(
    string BuildDirectory,
    string Layer,
    string? Backend,
    string? CorpusDirectory,
    string ResultsDirectory,
    string? Configuration,
    int DurationSeconds,
    int InputTimeoutSeconds,
    int MaxLength,
    bool ReplayOnly);
