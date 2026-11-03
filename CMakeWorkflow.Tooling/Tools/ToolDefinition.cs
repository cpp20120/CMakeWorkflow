using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling;

public sealed record ToolDefinition(
    string Id,
    string DisplayName,
    string[] Commands,
    string[] ProbeArguments,
    Version? MinimumVersion,
    IReadOnlyDictionary<PackageManagerKind, string[]> Packages,
    bool StandaloneCMake = false,
    string? ManualHint = null);
