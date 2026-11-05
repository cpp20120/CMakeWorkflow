#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

public sealed record VisualStudioInstance(
    string Id,
    string Name,
    string Version,
    string InstallationPath,
    string? DeveloperCommandPath);
