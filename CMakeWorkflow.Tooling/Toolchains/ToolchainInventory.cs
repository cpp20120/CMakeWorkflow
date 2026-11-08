
namespace CMakeWorkflow.Tooling.Toolchains;

public sealed record ToolchainInventory(
    int SchemaVersion,
    string Platform,

List<VisualStudioInstance> VisualStudio,

List<ToolchainCandidate> Candidates,

List<string> Diagnostics);
