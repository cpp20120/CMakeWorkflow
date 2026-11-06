using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Toolchains;

[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.Toolchains.ToolchainResolution(Diagnostics) participates in CMakeWorkflow.Client.Toolchains.ToolchainService.ResolveAsync; retain these concrete collection parameter types.")]
public sealed record ToolchainResolution(
    int SchemaVersion,
    string Status,
    ToolchainCandidate? CMake,
    ToolchainCandidate? Ninja,
    ToolchainCandidate? Compiler,
    ToolchainCandidate? Sdk,
    string? EnvironmentProfileId,
    [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.Toolchains.ToolchainResolution.Diagnostics (List<string>) is exposed through CMakeWorkflow.Client.Toolchains.ToolchainService.ResolveAsync; retain this Client SDK property signature.")]
List<string> Diagnostics);
