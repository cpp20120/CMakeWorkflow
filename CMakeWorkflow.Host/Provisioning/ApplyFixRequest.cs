using System.Text.Json.Serialization;
using CMakeWorkflow.Tooling;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using PolyType;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record ApplyFixRequest(string OperationId, string PlanId);
