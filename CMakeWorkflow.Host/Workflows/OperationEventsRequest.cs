using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record OperationEventsRequest(string OperationId, long AfterSequence = 0, int Limit = 100);
