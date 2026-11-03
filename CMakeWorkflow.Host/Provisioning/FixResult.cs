using System.Text.Json.Serialization;
using CMakeWorkflow.Tooling;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using PolyType;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record FixResult(
    int SchemaVersion,
    string Status,
    ToolingResult? Installation,
    ToolingPlan? Verification,
    string[] Diagnostics,
    bool ReadyToResume = false);
