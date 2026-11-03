namespace CMakeWorkflow;

public sealed class WorkflowNodeBuilder(WorkflowNode node, WorkflowBuilder owner)
{
    public WorkflowNodeBuilder DependsOn(params WorkflowNodeRef[] prerequisites)
    {
        ArgumentNullException.ThrowIfNull(prerequisites);
        foreach (var predecessor in prerequisites)
        {
            owner.RequireNode(predecessor);
            if (!node.DependsOn.Contains(predecessor.Id, StringComparer.Ordinal))
                node.DependsOn.Add(predecessor.Id);
        }

        return this;
    }

    public WorkflowNodeBuilder Arguments(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        node.Arguments.AddRange(values);
        return this;
    }

    public WorkflowNodeBuilder Timeout(TimeSpan value)
    {
        if (value <= TimeSpan.Zero || !double.IsFinite(value.TotalSeconds))
            throw new ArgumentOutOfRangeException(nameof(value));
        node.TimeoutSeconds = value.TotalSeconds;
        return this;
    }

    public WorkflowNodeBuilder Resource(string name)
    {
        node.Resources.Add(name);
        return this;
    }

    public WorkflowNodeBuilder Exclusive()
    {
        node.Exclusive = true;
        return this;
    }

    public WorkflowNodeBuilder WorkingDirectory(string path)
    {
        node.WorkingDirectory = path;
        return this;
    }

    public WorkflowNodeBuilder Environment(string name, string value)
    {
        node.Environment.Add(name, value);
        return this;
    }

    public WorkflowNodeBuilder Option(string name, string value)
    {
        node.Options.Add(name, value);
        return this;
    }

    public WorkflowNodeBuilder HarnessDirectory(string path)
    {
        node.HarnessDirectory = path;
        return this;
    }

    public WorkflowNodeBuilder TestRegex(string regex)
    {
        node.TestRegex = regex;
        return this;
    }

    public WorkflowNodeBuilder LabelRegex(string regex)
    {
        node.LabelRegex = regex;
        return this;
    }

    public WorkflowNodeBuilder RequireCpuSet(string cpuSet)
    {
        (node.Constraints ??= new()).CpuSet = cpuSet;
        return this;
    }

    public WorkflowNodeBuilder RequireProfiler(string backend, string mode, string? eventSource = null)
    {
        (node.Constraints ??= new()).Profiler = new()
            { Backend = backend, Mode = mode, EventSource = eventSource };
        return this;
    }

    public WorkflowNodeBuilder Input(string path, string? sha256 = null, bool snapshot = false)
    {
        node.Inputs.Add(new() { Path = path, Sha256 = sha256, Snapshot = snapshot });
        return this;
    }

    public WorkflowNodeBuilder InputFrom(WorkflowNodeRef producer, string artifact, string? path = null)
    {
        owner.RequireNode(producer);
        node.Inputs.Add(new() { From = producer.Id, Artifact = artifact, Path = path });
        return this;
    }

    public WorkflowNodeBuilder Output(string name, string path, string root = "manifest", bool fresh = false)
    {
        node.Outputs.Add(new() { Name = name, Path = path, Root = root, Fresh = fresh });
        return this;
    }
}