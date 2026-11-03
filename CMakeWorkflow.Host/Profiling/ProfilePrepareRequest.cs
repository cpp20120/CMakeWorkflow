using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record ProfilePrepareRequest(
    string RequestId,
    string ResultsDirectory,
    string Target,
    List<ProfileVariantRequest> Variants,
    int Repeats = 3,
    double TimeoutSeconds = 300,
    string Backend = "auto",
    string Mode = "cpu",
    string? Configuration = null,
    string? SourceDirectory = null,
    string? ConfigurePreset = null,
    string[]? Arguments = null);
