namespace CMakeWorkflow.Core.Execution;

// These contracts describe a finite execution graph, not a CMake workflow.
// The domain adapter owns the meaning and persistence of each node state.
/// <summary>Domain-neutral states consulted for dependency admission and interruption.</summary>
/// <remarks>
/// Succeeded satisfies prerequisites; failed, blocked and canceled block dependents. NeedsAttention stops
/// new admissions while already active work drains. Running is an admission intent, not proof of process start.
/// OutcomeUnknown carries unresolved effects; the domain adapter must validate safe recovery before scheduling.
/// </remarks>
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

/// <summary>Scheduler input whose state and payload semantics remain owned by the domain adapter.</summary>
/// <remarks>
/// IDs and dependency references must already form a validated acyclic graph. The scheduler reads this
/// definition without cloning it; callers must not mutate it concurrently. Exclusive selects the next candidate
/// batch, while Conflicts must also enforce exclusion against active nodes.
/// </remarks>
/// <param name="Id">Unique graph identifier used by the state callbacks.</param>
/// <param name="Dependencies">Non-null prerequisite IDs; only succeeded dependencies permit admission.</param>
/// <param name="Exclusive">Whether this node takes priority as the sole candidate in a ready batch.</param>
/// <param name="Payload">Borrowed domain data; scheduler does not interpret or persist it.</param>
internal sealed record ExecutionNode<TPayload>(
    string Id,
    IReadOnlyList<string> Dependencies,
    bool Exclusive,
    TPayload Payload);

internal sealed record ExecutionPlan<TPayload>(IReadOnlyList<ExecutionNode<TPayload>> Nodes);

// A single adapter controls all external effects: checkpoint, authorization,
// node execution, and result integration. The scheduler never serializes state,
// acquires domain leases, or interprets domain evidence.
/// <summary>Domain adapter callbacks defining state, persistence and external effects.</summary>
/// <remarks>
/// The scheduler invokes Reserve for a batch, then awaits PersistCheckpoint, then ConfirmDispatch (when supplied),
/// and only then starts executors. Reserve must record running intent without launching external work.
/// The adapter owns state validation, durable evidence, leases and resource cleanup; this contract does not supply them.
/// Execute must return a task representing the entire worker lifetime, including cleanup, so leases can safely outlive execution.
/// </remarks>
internal sealed class ExecutionSchedulerHooks<TPayload, TResult, TReservation>
{
    /// <summary>Reads authoritative adapter state for a node ID.</summary>
    internal required Func<string, ExecutionStatus> GetStatus { get; init; }
    /// <summary>Reports whether two nodes cannot overlap, including domain resources and exclusivity.</summary>
    internal required Func<ExecutionNode<TPayload>, ExecutionNode<TPayload>, bool> Conflicts { get; init; }
    /// <summary>Marks a not-yet-admitted node canceled when caller cancellation is observed.</summary>
    internal required Action<ExecutionNode<TPayload>> MarkCanceled { get; init; }
    /// <summary>Marks a node blocked by a failed, blocked or canceled prerequisite.</summary>
    internal required Action<ExecutionNode<TPayload>> MarkBlocked { get; init; }
    /// <summary>Validates/prepares admission before recording running intent; must not launch the worker.</summary>
    internal required Func<ExecutionNode<TPayload>, CancellationToken, Task> BeforeReserve { get; init; }
    /// <summary>Records running intent and returns execution resources without external dispatch.</summary>
    internal required Func<ExecutionNode<TPayload>, TReservation> Reserve { get; init; }
    /// <summary>Durably persists the complete batch of running intents before any executor starts.</summary>
    internal required Func<Task> PersistCheckpoint { get; init; }
    /// <summary>Optional authorization gate after durable persistence; failure prevents this batch from launching.</summary>
    internal Func<CancellationToken, Task>? ConfirmDispatch { get; init; }
    /// <summary>Runs after checkpoint/gate and before each executor; may interrupt dispatch.</summary>
    internal required Func<ExecutionNode<TPayload>, CancellationToken, Task> BeforeExecute { get; init; }
    /// <summary>Runs the worker with the stop token; its task must cover cleanup and release of worker-owned resources.</summary>
    internal required Func<ExecutionNode<TPayload>, TReservation, CancellationToken, Task<TResult>> Execute { get; init; }
    /// <summary>Runs after worker completion, before normal result integration.</summary>
    internal required Func<ExecutionNode<TPayload>, CancellationToken, Task> AfterExecute { get; init; }
    /// <summary>Integrates a normally observed worker result into authoritative adapter state.</summary>
    internal required Action<ExecutionNode<TPayload>, TResult> Complete { get; init; }
    /// <summary>Integrates a result observed during interrupted-scheduler drain.</summary>
    internal required Action<ExecutionNode<TPayload>, TResult> DrainInFlight { get; init; }
}
