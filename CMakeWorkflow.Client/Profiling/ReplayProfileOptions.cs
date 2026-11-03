using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record ReplayProfileOptions(
    string BaselineDirectory,
    string CandidateDirectory,
    string NodeId,
    string ResultsDirectory,
    string? CaseName = null,
    int HarnessIndex = 0,
    string Backend = "auto",
    double TimeoutSeconds = 300);
