using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Toolchains;

[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.Toolchains.DeveloperEnvironmentSnapshot(Diagnostics) participates in CMakeWorkflow.Client.Toolchains.ToolchainService.InspectEnvironmentAsync; retain these concrete collection parameter types.")]
public sealed record DeveloperEnvironmentSnapshot(
    int SchemaVersion,
    string EnvironmentProfileId,
    string HostArchitecture,
    string TargetArchitecture,
    string Fingerprint,
    Dictionary<string, string> VisibleVariables,
    [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.Toolchains.DeveloperEnvironmentSnapshot.Diagnostics (List<string>) is exposed through CMakeWorkflow.Client.Toolchains.ToolchainService.InspectEnvironmentAsync; retain this Client SDK property signature.")]
List<string> Diagnostics);
