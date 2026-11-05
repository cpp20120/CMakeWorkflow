#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

// Independent SDK models: the client references only Contracts, never Host.
public sealed record ToolchainCandidate(
    string Id,
    string Role,
    string Flavor,
    string Provider,
    string Path,
    string? Version = null,
    string? VisualStudioInstanceId = null,
    string? EnvironmentProfileId = null);
