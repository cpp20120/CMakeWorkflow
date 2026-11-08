namespace CMakeWorkflow.Tooling.Tools;

public sealed record ToolProfile(string Id, string Description,
#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Tools
#pragma warning restore CA1819
);
