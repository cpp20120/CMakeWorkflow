using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record BenchmarkPrepareRequest(
    string RequestId,
    string OutputPath,
    BenchmarkRecipeV1 Recipe);
