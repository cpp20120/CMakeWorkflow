using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling;

public sealed record ToolProfile(string Id, string Description, string[] Tools);
