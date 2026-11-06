using System.Diagnostics.CodeAnalysis;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Toolchains;
#pragma warning restore IDE0130

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record ToolchainInventory(
    int SchemaVersion,
    string Platform,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<VisualStudioInstance> VisualStudio,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<ToolchainCandidate> Candidates,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<string> Diagnostics);
