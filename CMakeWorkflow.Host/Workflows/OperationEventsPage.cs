using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record OperationEventsPage(
    string OperationId,
    long AfterSequence,
    long NextSequence,
    bool HasMore,
    List<WorkflowEvent> Events);
