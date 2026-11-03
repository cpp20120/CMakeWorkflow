using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling;

public sealed record ToolingProfileList(int SchemaVersion, List<ToolingProfileView> Profiles);
