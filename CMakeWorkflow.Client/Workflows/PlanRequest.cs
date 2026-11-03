using System.Text.Json;
using System.Text.Json.Serialization;
using PolyType;
using StreamJsonRpc;
using CMakeWorkflow;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Client;

internal sealed record PlanRequest(string Manifest, string[]? Only = null, string? ControllerBuildDirectory = null);
