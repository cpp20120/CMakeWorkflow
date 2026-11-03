using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record TestMatrixPrepareRequest(string RequestId, string OutputPath, TestMatrixRecipeV1 Recipe);
