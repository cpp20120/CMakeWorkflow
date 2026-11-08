using CMakeWorkflow.CMake.Recovery;

namespace CMakeWorkflow.Host.Workflows;

public sealed record OperationEventsPage(
    string OperationId,
    long AfterSequence,
    long NextSequence,
    bool HasMore,

List<WorkflowEvent> Events);
