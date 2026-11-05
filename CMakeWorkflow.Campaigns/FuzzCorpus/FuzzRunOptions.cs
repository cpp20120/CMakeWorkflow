#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Campaigns;
#pragma warning restore IDE0130

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
