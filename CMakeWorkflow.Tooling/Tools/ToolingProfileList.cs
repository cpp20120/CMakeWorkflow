
// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Tools;
#pragma warning restore IDE0130

public sealed record ToolingProfileList(int SchemaVersion,
List<ToolingProfileView> Profiles);
