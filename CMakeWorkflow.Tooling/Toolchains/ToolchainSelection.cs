using System.Text.Json.Serialization;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public sealed record ToolchainSelection(
    [property: JsonPropertyName("cmakeCandidateId")]
    string? CMakeCandidateId = null,
    string? NinjaCandidateId = null,
    string? CompilerCandidateId = null,
    string? SdkCandidateId = null,
    string? EnvironmentProfileId = null,
    string Generator = "Ninja");
