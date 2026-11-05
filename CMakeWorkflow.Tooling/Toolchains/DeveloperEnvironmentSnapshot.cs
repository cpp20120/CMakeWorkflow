using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record DeveloperEnvironmentSnapshot(
    int SchemaVersion,
    string EnvironmentProfileId,
    string HostArchitecture,
    string TargetArchitecture,
    string Fingerprint,
    Dictionary<string, string> VisibleVariables,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<string> Diagnostics);
