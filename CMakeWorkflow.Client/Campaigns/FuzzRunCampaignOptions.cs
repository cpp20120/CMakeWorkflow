using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

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
