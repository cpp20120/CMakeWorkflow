using System.Diagnostics.CodeAnalysis;
using CMakeWorkflow.CMake.Recovery;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record OperationEventsPage(
    string OperationId,
    long AfterSequence,
    long NextSequence,
    bool HasMore,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<WorkflowEvent> Events);
