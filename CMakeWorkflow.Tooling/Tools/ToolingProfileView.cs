using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling;

public sealed record ToolingProfileView(string Id, string Description, string[] Tools);
