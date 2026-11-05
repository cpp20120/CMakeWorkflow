namespace CMakeWorkflow.Client.Toolchains;

public sealed record DeveloperEnvironmentRequest(
    string EnvironmentProfileId,
    string HostArchitecture = "x64",
    string TargetArchitecture = "x64",
    string? WindowsSdkVersion = null,
    string? ToolsetVersion = null);
