// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Toolchains;
#pragma warning restore IDE0130

public sealed record VisualStudioInstance(
    string Id,
    string Name,
    string Version,
    string InstallationPath,
    string? DeveloperCommandPath);
