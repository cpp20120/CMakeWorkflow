using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record BuildMatrixPrepareRequest(string RequestId, string OutputPath, BuildMatrixRecipeV1 Recipe);
