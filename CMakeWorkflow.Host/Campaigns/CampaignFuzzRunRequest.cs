using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

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
