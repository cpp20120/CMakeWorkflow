using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record PrepareProfileOptions(
    string RequestId,
    string ResultsDirectory,
    string Target,
    List<ProfileVariantOptions> Variants,
    int Repeats = 3,
    double TimeoutSeconds = 300,
    string Backend = "auto",
    string Mode = "cpu",
    string? Configuration = null,
    string? SourceDirectory = null,
    string? ConfigurePreset = null,
    string[]? Arguments = null);
