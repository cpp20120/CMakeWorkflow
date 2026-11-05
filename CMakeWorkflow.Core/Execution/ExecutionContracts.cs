#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Engine;
#pragma warning restore IDE0130

// These contracts describe a finite execution graph, not a CMake workflow.
// The domain adapter owns the meaning and persistence of each node state.
internal enum ExecutionStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Blocked,
    Canceled,
    NeedsAttention,
    OutcomeUnknown
}

internal sealed record ExecutionNode<TPayload>(
    string Id,
    IReadOnlyList<string> Dependencies,
    bool Exclusive,
    TPayload Payload);

internal sealed record ExecutionPlan<TPayload>(IReadOnlyList<ExecutionNode<TPayload>> Nodes);

// A single adapter controls all external effects: checkpoint, authorization,
// node execution, and result integration. The scheduler never serializes state,
// acquires domain leases, or interprets domain evidence.
internal sealed class ExecutionSchedulerHooks<TPayload, TResult, TReservation>
{
    internal required Func<string, ExecutionStatus> GetStatus { get; init; }
    internal required Func<ExecutionNode<TPayload>, ExecutionNode<TPayload>, bool> Conflicts { get; init; }
    internal required Action<ExecutionNode<TPayload>> MarkCanceled { get; init; }
    internal required Action<ExecutionNode<TPayload>> MarkBlocked { get; init; }
    internal required Func<ExecutionNode<TPayload>, CancellationToken, Task> BeforeReserve { get; init; }
    internal required Func<ExecutionNode<TPayload>, TReservation> Reserve { get; init; }
    internal required Func<Task> PersistCheckpoint { get; init; }
    internal Func<CancellationToken, Task>? ConfirmDispatch { get; init; }
    internal required Func<ExecutionNode<TPayload>, CancellationToken, Task> BeforeExecute { get; init; }
    internal required Func<ExecutionNode<TPayload>, TReservation, CancellationToken, Task<TResult>> Execute { get; init; }
    internal required Func<ExecutionNode<TPayload>, CancellationToken, Task> AfterExecute { get; init; }
    internal required Action<ExecutionNode<TPayload>, TResult> Complete { get; init; }
    internal required Action<ExecutionNode<TPayload>, TResult> DrainInFlight { get; init; }
}
