using CMakeWorkflow.Engine;

// An independent, in-memory consumer: there are no CMake objects, files,
// process launches, report models or Host RPC dependencies in this fixture.
internal static class NeutralExecutionSchedulerTests
{
    public static async Task RunAsync()
    {
        await DependentGraphAndGateAsync();
        await ParallelBatchAsync();
        await ExclusiveAndConflictAsync();
        await FailureBlocksDependentAsync();
        await NeedsAttentionStopsAdmissionAsync();
        await FailedCheckpointBlocksDispatchAsync();
        await FaultDrainsActiveTasksAsync();
        Console.WriteLine("Neutral execution scheduler checks passed.");
    }

    private static async Task DependentGraphAndGateAsync()
    {
        List<string> events = [];
        ExecutionNode<string>[] nodes =
        [
            Node("prepare"), Node("run", "prepare"), Node("verify", "run")
        ];
        Dictionary<string, ExecutionStatus> states = await ExecuteAsync(nodes, events, _ => ExecutionStatus.Succeeded, jobs: 2);
        Check(states.Values.All(s => s == ExecutionStatus.Succeeded), "dependent graph must succeed");
        Equal(["reserve:prepare", "reserve:run", "reserve:verify"],
            events.Where(e => e.StartsWith("reserve:", StringComparison.Ordinal)),
            "dependency admission order");
        Check(events.Count(e => e == "gate") == 3, "each nonempty batch is confirmed once");
        Check(events.IndexOf("save") < events.IndexOf("gate") &&
              events.IndexOf("gate") < events.IndexOf("execute:prepare"),
            "durable save must precede gate and external dispatch");
    }

    private static async Task ParallelBatchAsync()
    {
        List<string> events = [];
        Dictionary<string, ExecutionStatus> states = await ExecuteAsync([Node("first"), Node("second")], events,
            _ => ExecutionStatus.Succeeded, jobs: 2);
        Check(states.Values.All(s => s == ExecutionStatus.Succeeded), "independent batch must succeed");
        Equal(["reserve:first", "reserve:second"],
            events.Where(e => e.StartsWith("reserve:", StringComparison.Ordinal)),
            "two ready nodes admitted to the same batch");
        Check(events.Count(e => e == "gate") == 1, "one confirmation covers the complete batch");
        Check(events.IndexOf("reserve:second") < events.IndexOf("save") &&
              events.IndexOf("save") < events.IndexOf("execute:first") &&
              events.IndexOf("save") < events.IndexOf("execute:second"),
            "the batch must commit both running states before either executor starts");
    }

    private static async Task ExclusiveAndConflictAsync()
    {
        List<string> events = [];
        ExecutionNode<string>[] nodes = [Node("alpha"), Node("beta"), Node("exclusive", exclusive: true)];
        Dictionary<string, ExecutionStatus> states = await ExecuteAsync(nodes, events, _ => ExecutionStatus.Succeeded, jobs: 2,
            conflict: (left, right) => (left == "alpha" && right == "beta") ||
                                       (left == "beta" && right == "alpha"));
        Check(states.Values.All(s => s == ExecutionStatus.Succeeded), "resource-conflict graph must succeed");
        Equal(["reserve:exclusive", "reserve:alpha", "reserve:beta"],
            events.Where(e => e.StartsWith("reserve:", StringComparison.Ordinal)),
            "exclusive drains ahead of ready work; conflicts serialize");
    }

    private static async Task FailureBlocksDependentAsync()
    {
        List<string> events = [];
        Dictionary<string, ExecutionStatus> states = await ExecuteAsync([Node("fail"), Node("blocked", "fail"), Node("unrelated")],
            events, id => id == "fail" ? ExecutionStatus.Failed : ExecutionStatus.Succeeded, jobs: 2);
        Check(states["fail"] == ExecutionStatus.Failed &&
              states["blocked"] == ExecutionStatus.Blocked &&
              states["unrelated"] == ExecutionStatus.Succeeded, "failure propagation");
        Check(!events.Contains("execute:blocked"), "blocked dependent must not dispatch");
    }

    private static async Task NeedsAttentionStopsAdmissionAsync()
    {
        List<string> events = [];
        Dictionary<string, ExecutionStatus> states = await ExecuteAsync(
            [Node("missing"), Node("independent"), Node("downstream", "independent")],
            events, id => id == "missing" ? ExecutionStatus.NeedsAttention : ExecutionStatus.Succeeded,
            jobs: 2);
        Check(states["missing"] == ExecutionStatus.NeedsAttention &&
              states["independent"] == ExecutionStatus.Succeeded &&
              states["downstream"] == ExecutionStatus.Pending,
            "needs_attention halts new admissions after already-started tasks finish");
        Check(!events.Contains("execute:downstream"), "no new effects after needs_attention");
    }

    private static async Task FailedCheckpointBlocksDispatchAsync()
    {
        List<string> events = [];
        try
        {
            _ = await ExecuteAsync([Node("unsafe")], events, _ => ExecutionStatus.Succeeded,
                jobs: 1, failOnCheckpoint: true);
            throw new System.Diagnostics.UnreachableException("Expected checkpoint failure.");
        }
        catch (IOException error) when (error.Message == "injected checkpoint failure")
        {
            Check(events.Contains("reserve:unsafe") && events.Contains("save"),
                "reservation must precede failed checkpoint");
            Check(!events.Contains("gate") && !events.Contains("execute:unsafe"),
                "checkpoint failure must prevent confirmation and dispatch");
        }
    }

    private static async Task FaultDrainsActiveTasksAsync()
    {
        List<string> events = [];
        try
        {
            _ = await ExecuteAsync([Node("left"), Node("right")], events,
                _ => ExecutionStatus.Succeeded, jobs: 2, failAfterExecution: true);
            throw new System.Diagnostics.UnreachableException("Expected injected post-execution failure.");
        }
        catch (InvalidOperationException error) when (error.Message == "injected post-execution failure")
        {
            Check(events.Contains("drain:left") && events.Contains("drain:right"),
                "a failed completion callback must drain all admitted workers");
        }
    }

    private static ExecutionNode<string> Node(string id, string? dependency = null, bool exclusive = false) =>
        new(id, dependency is null ? [] : [dependency], exclusive, id);

    private static async Task<Dictionary<string, ExecutionStatus>> ExecuteAsync(
        ExecutionNode<string>[] nodes, List<string> events,
        Func<string, ExecutionStatus> outcome, int jobs,
        Func<string, string, bool>? conflict = null, bool failOnCheckpoint = false,
        bool failAfterExecution = false)
    {
        Dictionary<string, ExecutionStatus> states = nodes.ToDictionary(n => n.Id, _ => ExecutionStatus.Pending, StringComparer.Ordinal);
        bool committed = false;
        bool authorized = false;
        using CancellationTokenSource stop = new();
        await ExecutionScheduler.RunAsync(new ExecutionPlan<string>(nodes), jobs,
stop,
            new ExecutionSchedulerHooks<string, ExecutionStatus, string>
            {
                GetStatus = id => states[id],
                Conflicts = (a, b) => a.Exclusive || b.Exclusive ||
                                       (conflict?.Invoke(a.Id, b.Id) ?? false),
                MarkCanceled = node => states[node.Id] = ExecutionStatus.Canceled,
                MarkBlocked = node => states[node.Id] = ExecutionStatus.Blocked,
                BeforeReserve = (_, _) => Task.CompletedTask,
                Reserve = node =>
                {
                    events.Add("reserve:" + node.Id);
                    states[node.Id] = ExecutionStatus.Running;
                    return node.Id;
                },
                PersistCheckpoint = () =>
                {
                    events.Add("save");
                    committed = false;
                    authorized = false;
                    if (failOnCheckpoint)
                    {
                        throw new IOException("injected checkpoint failure");
                    }

                    committed = true;
                    return Task.CompletedTask;
                },
                ConfirmDispatch = _ =>
                {
                    Check(committed, "dispatch authorization without a completed checkpoint");
                    events.Add("gate");
                    authorized = true;
                    return Task.CompletedTask;
                },
                BeforeExecute = (_, _) => Task.CompletedTask,
                Execute = (node, _, _) =>
                {
                    Check(committed && authorized && states[node.Id] == ExecutionStatus.Running,
                        "executor ran before checkpoint and gate");
                    events.Add("execute:" + node.Id);
                    return Task.FromResult(outcome(node.Id));
                },
                AfterExecute = (_, _) => failAfterExecution
                    ? Task.FromException(new InvalidOperationException("injected post-execution failure"))
                    : Task.CompletedTask,
                Complete = (node, result) => states[node.Id] = result,
                DrainInFlight = (node, result) =>
                {
                    events.Add("drain:" + node.Id);
                    states[node.Id] = result;
                }
            }, cancellation: CancellationToken.None);
        return states;
    }

    private static void Equal(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new System.Diagnostics.UnreachableException($"{message}: expected {string.Join(",", expected)}, got {string.Join(",", actual)}");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException("Neutral Execution Engine: " + message);
        }
    }
}
