using System.Text.Json.Serialization;

namespace CMakeWorkflow;

// A request for toolchain binding, deliberately independent of Windows discovery DTOs.
// The Host (or CLI) resolves IDs afresh against its own inventory. Never trust client paths.
public sealed record WorkflowToolchainBinding(
    [property: JsonPropertyName("cmakeCandidateId")]
    string? CMakeCandidateId = null,
    string? NinjaCandidateId = null,
    string? CompilerCandidateId = null,
    string? SdkCandidateId = null,
    string? EnvironmentProfileId = null,
    string Generator = "Ninja",
    string HostArchitecture = "x64",
    string TargetArchitecture = "x64");