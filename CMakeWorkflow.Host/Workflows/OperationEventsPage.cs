using CMakeWorkflow.CMake.Recovery;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

public sealed record OperationEventsPage(
    string OperationId,
    long AfterSequence,
    long NextSequence,
    bool HasMore,

List<WorkflowEvent> Events);
