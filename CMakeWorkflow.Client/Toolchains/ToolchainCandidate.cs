namespace CMakeWorkflow.Client.Toolchains;

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
