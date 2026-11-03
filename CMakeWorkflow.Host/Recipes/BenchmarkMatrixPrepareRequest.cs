using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record BenchmarkMatrixPrepareRequest(string RequestId, string OutputPath, BenchmarkMatrixRecipeV1 Recipe);
