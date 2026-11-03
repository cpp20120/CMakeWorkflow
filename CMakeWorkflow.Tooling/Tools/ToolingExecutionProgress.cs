using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling;

public sealed record ToolingExecutionProgress(string Tool, string Stage, string? Message);
