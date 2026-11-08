using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Contracts.Builders;

/// <summary>Mutable fluent adapter over a workflow DTO.</summary>
/// <remarks>
/// Methods only modify the supplied DTO and return this builder. They perform no execution or persistence.
/// The caller owns the DTO; planning remains authoritative for semantic validation.
/// </remarks>
public sealed class WorkflowNodeBuilder(WorkflowNode node, WorkflowBuilder owner)
{
    /// <summary>Adds unique prerequisite IDs; every reference must already exist in the owner builder.</summary>
    public WorkflowNodeBuilder DependsOn(params WorkflowNodeRef[] prerequisites)
    {
        ArgumentNullException.ThrowIfNull(prerequisites);
        foreach (WorkflowNodeRef predecessor in prerequisites)
        {
            owner.RequireNode(predecessor);
            if (!node.DependsOn.Contains(predecessor.Id, StringComparer.Ordinal))
            {
                node.DependsOn.Add(predecessor.Id);
            }
        }

        return this;
    }

    /// <summary>Appends literal process arguments; does not parse a shell command.</summary>
    public WorkflowNodeBuilder Arguments(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        node.Arguments.AddRange(values);
        return this;
    }

    /// <summary>Stores a positive finite timeout as seconds; the planner enforces its 30-day maximum.</summary>
    public WorkflowNodeBuilder Timeout(TimeSpan value)
    {
        if (value <= TimeSpan.Zero || !double.IsFinite(value.TotalSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        node.TimeoutSeconds = value.TotalSeconds;
        return this;
    }

    /// <summary>Adds a run-local scheduling conflict key.</summary>
    public WorkflowNodeBuilder Resource(string name)
    {
        node.Resources.Add(name);
        return this;
    }

    /// <summary>Requests exclusive scheduling relative to other active nodes in this run.</summary>
    public WorkflowNodeBuilder Exclusive()
    {
        node.Exclusive = true;
        return this;
    }

    /// <summary>Sets an execution directory; relative paths use the manifest base.</summary>
    public WorkflowNodeBuilder WorkingDirectory(string path)
    {
        node.WorkingDirectory = path;
        return this;
    }

    /// <summary>Adds an environment override; duplicate keys throw.</summary>
    public WorkflowNodeBuilder Environment(string name, string value)
    {
        node.Environment.Add(name, value);
        return this;
    }

    /// <summary>Adds a profile option; duplicate keys throw and other kinds reject options during planning.</summary>
    public WorkflowNodeBuilder Option(string name, string value)
    {
        node.Options.Add(name, value);
        return this;
    }

    /// <summary>Sets an invoke output directory relative to the native build tree.</summary>
    public WorkflowNodeBuilder HarnessDirectory(string path)
    {
        node.HarnessDirectory = path;
        return this;
    }

    /// <summary>Sets the CTest name filter; planning rejects this field for other kinds.</summary>
    public WorkflowNodeBuilder TestRegex(string regex)
    {
        node.TestRegex = regex;
        return this;
    }

    /// <summary>Sets the CTest label filter; planning rejects this field for other kinds.</summary>
    public WorkflowNodeBuilder LabelRegex(string regex)
    {
        node.LabelRegex = regex;
        return this;
    }

    /// <summary>Requires CPU affinity for run/profile execution.</summary>
    public WorkflowNodeBuilder RequireCpuSet(string cpuSet)
    {
        (node.Constraints ??= new()).CpuSet = cpuSet;
        return this;
    }

    /// <summary>Requires an explicit matching profiler backend/mode; null eventSource adds no source requirement.</summary>
    public WorkflowNodeBuilder RequireProfiler(string backend, string mode, string? eventSource = null)
    {
        (node.Constraints ??= new()).Profiler = new()
        { Backend = backend, Mode = mode, EventSource = eventSource };
        return this;
    }

    /// <summary>Declares filesystem evidence; null sha256 omits a caller hash expectation, snapshot retains a verified copy.</summary>
    public WorkflowNodeBuilder Input(string path, string? sha256 = null, bool snapshot = false)
    {
        node.Inputs.Add(new() { Path = path, Sha256 = sha256, Snapshot = snapshot });
        return this;
    }

    /// <summary>Declares a named output from an existing producer; contributes a DAG dependency. Null path uses the producer path.</summary>
    public WorkflowNodeBuilder InputFrom(WorkflowNodeRef producer, string artifact, string? path = null)
    {
        owner.RequireNode(producer);
        node.Inputs.Add(new() { From = producer.Id, Artifact = artifact, Path = path });
        return this;
    }

    /// <summary>Declares named file evidence; root is manifest or run, and fresh requires absence before execution.</summary>
    public WorkflowNodeBuilder Output(string name, string path, string root = "manifest", bool fresh = false)
    {
        node.Outputs.Add(new() { Name = name, Path = path, Root = root, Fresh = fresh });
        return this;
    }
}