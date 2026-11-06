
// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Toolchains;
#pragma warning restore IDE0130

public sealed record ToolchainInventory(
    int SchemaVersion,
    string Platform,

List<VisualStudioInstance> VisualStudio,

List<ToolchainCandidate> Candidates,

List<string> Diagnostics);
