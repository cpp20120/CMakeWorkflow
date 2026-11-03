using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMakeWorkflow.Profiling;

public sealed record ProfileReplayOptions(
    string BaselineDirectory,
    string CandidateDirectory,
    string NodeId,
    string? CaseName = null,
    int HarnessIndex = 0,
    string Backend = "auto",
    string ResultsDirectory = "out/profiles",
    double TimeoutSeconds = 300);
