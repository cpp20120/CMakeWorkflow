using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

public sealed record ToolchainSelection(
    [property: JsonPropertyName("cmakeCandidateId")]
    string? CMakeCandidateId = null,
    string? NinjaCandidateId = null,
    string? CompilerCandidateId = null,
    string? SdkCandidateId = null,
    string? EnvironmentProfileId = null,
    string Generator = "Ninja");
