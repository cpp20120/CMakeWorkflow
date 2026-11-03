using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

// These analysis/replay/campaign calls are request-scoped, unlike workflow/start.
// A disconnect cancels their token; never assume durable acceptance or retry.
public sealed record ProfileReplayRequest(
    string BaselineDirectory,
    string CandidateDirectory,
    string NodeId,
    string ResultsDirectory,
    string? CaseName = null,
    int HarnessIndex = 0,
    string Backend = "auto",
    double TimeoutSeconds = 300);
