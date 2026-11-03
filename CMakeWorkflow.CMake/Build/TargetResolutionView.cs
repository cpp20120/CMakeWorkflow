using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow;

public sealed record TargetResolutionView(
    int SchemaVersion,
    string BuildDirectory,
    string Target,
    string? Configuration,
    string Executable);
