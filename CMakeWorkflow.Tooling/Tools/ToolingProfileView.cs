// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Tools;
#pragma warning restore IDE0130

public sealed record ToolingProfileView(string Id, string Description,
#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[] Tools
#pragma warning restore CA1819
);
