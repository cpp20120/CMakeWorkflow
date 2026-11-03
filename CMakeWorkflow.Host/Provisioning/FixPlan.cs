using System.Text.Json.Serialization;
using CMakeWorkflow.Tooling;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using PolyType;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record FixPlan(
    int SchemaVersion,
    string PlanId,
    DateTimeOffset ExpiresAt,
    ToolingPlan Plan,
    string? RunDirectory = null,
    string? RunSnapshot = null);
