using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling;

public sealed record ToolingResult(
    int SchemaVersion,
    bool Success,
    string[] Ready,
    string[] Failed,
    string[] Diagnostics);
