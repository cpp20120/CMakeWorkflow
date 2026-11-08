using System.Text.Json.Serialization;

namespace CMakeWorkflow.Contracts.Workflows;

// A request for toolchain binding, deliberately independent of Windows discovery DTOs.
// The Host (or CLI) resolves IDs afresh against its own inventory. Never trust client paths.
/// <summary>Host-local toolchain identity requests; candidate IDs are never executable paths.</summary>
/// <remarks>
/// The execution host resolves IDs afresh and validates build-tree identity before dispatch.
/// Null candidate/profile IDs leave that selection unspecified.
/// </remarks>
/// <param name="CMakeCandidateId">CMake inventory ID, or null.</param>
/// <param name="NinjaCandidateId">Ninja inventory ID, or null.</param>
/// <param name="CompilerCandidateId">Compiler inventory ID, or null.</param>
/// <param name="SdkCandidateId">SDK inventory ID, or null.</param>
/// <param name="EnvironmentProfileId">Developer environment profile ID, or null.</param>
/// <param name="Generator">Requested generator identity; defaults to Ninja.</param>
/// <param name="HostArchitecture">Tool-host architecture identifier; defaults to x64.</param>
/// <param name="TargetArchitecture">Compilation target architecture identifier; defaults to x64.</param>
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