#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

// Keep the environment in memory; never put inherited variables or tokens in run.json.
public sealed record WorkflowToolchainRuntime(
    string CMake,
    string? Ninja,
    string Compiler,
    string? Sdk,
    string? EnvironmentProfileId,
    string Generator,
    string Fingerprint,
    IReadOnlyDictionary<string, string> Variables);
