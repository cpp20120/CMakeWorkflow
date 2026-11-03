using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.Campaigns;

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
