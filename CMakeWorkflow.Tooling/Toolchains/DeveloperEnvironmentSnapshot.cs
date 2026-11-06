
// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Toolchains;
#pragma warning restore IDE0130

public sealed record DeveloperEnvironmentSnapshot(
    int SchemaVersion,
    string EnvironmentProfileId,
    string HostArchitecture,
    string TargetArchitecture,
    string Fingerprint,
    Dictionary<string, string> VisibleVariables,

List<string> Diagnostics);
