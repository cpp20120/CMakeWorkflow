using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record WorkflowPlanRequest(
    string Manifest,
    string[]? Only = null,
    string? ControllerBuildDirectory = null);
