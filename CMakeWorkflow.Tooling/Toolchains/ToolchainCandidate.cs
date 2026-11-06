// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Toolchains;
#pragma warning restore IDE0130

// Discovery is evidence, not a promise that a particular compiler/SDK ABI works.
// Candidate IDs derive from role + canonical location; they are not client-supplied paths.
public sealed record ToolchainCandidate(
    string Id,
    string Role,
    string Flavor,
    string Provider,
    string Path,
    string? Version = null,
    string? VisualStudioInstanceId = null,
    string? EnvironmentProfileId = null);
