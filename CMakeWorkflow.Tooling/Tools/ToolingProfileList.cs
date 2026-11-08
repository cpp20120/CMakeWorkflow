
namespace CMakeWorkflow.Tooling.Tools;

public sealed record ToolingProfileList(int SchemaVersion,
List<ToolingProfileView> Profiles);
