#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

public sealed record DeveloperEnvironmentRequest(
    string EnvironmentProfileId,
    string HostArchitecture = "x64",
    string TargetArchitecture = "x64",
    string? WindowsSdkVersion = null,
    string? ToolsetVersion = null);
