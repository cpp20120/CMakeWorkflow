#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed record WorkflowToolchainIdentity(
    string BuildDirectory,
    string CMake,
    string? Ninja,
    string Compiler,
    string? Sdk,
    string? EnvironmentProfileId,
    string Generator,
    string Fingerprint);
