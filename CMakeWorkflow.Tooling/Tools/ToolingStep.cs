using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling;

public sealed record ToolingStep(
    string Tool,
    string Method,
    string? Executable,
    string[] Arguments,
    string? Source,
    string? Diagnostic);
