namespace CMakeWorkflow.Tooling.Toolchains;

public sealed record VisualStudioInstance(
    string Id,
    string Name,
    string Version,
    string InstallationPath,
    string? DeveloperCommandPath);
