
// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Toolchains;
#pragma warning restore IDE0130

public sealed record ToolchainResolution(
    int SchemaVersion,
    string Status,
    ToolchainCandidate? CMake,
    ToolchainCandidate? Ninja,
    ToolchainCandidate? Compiler,
    ToolchainCandidate? Sdk,
    string? EnvironmentProfileId,

List<string> Diagnostics);
