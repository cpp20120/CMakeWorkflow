using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Toolchains;

[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.Toolchains.ToolchainInventory(VisualStudio, Candidates, Diagnostics) participates in CMakeWorkflow.Client.Toolchains.ToolchainService.DiscoverAsync; retain these concrete collection parameter types.")]
public sealed record ToolchainInventory(
    int SchemaVersion,
    string Platform,
    [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.Toolchains.ToolchainInventory.VisualStudio (List<VisualStudioInstance>) is exposed through CMakeWorkflow.Client.Toolchains.ToolchainService.DiscoverAsync; retain this Client SDK property signature.")]
List<VisualStudioInstance> VisualStudio,
    [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.Toolchains.ToolchainInventory.Candidates (List<ToolchainCandidate>) is exposed through CMakeWorkflow.Client.Toolchains.ToolchainService.DiscoverAsync; retain this Client SDK property signature.")]
List<ToolchainCandidate> Candidates,
    [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.Toolchains.ToolchainInventory.Diagnostics (List<string>) is exposed through CMakeWorkflow.Client.Toolchains.ToolchainService.DiscoverAsync; retain this Client SDK property signature.")]
List<string> Diagnostics);
