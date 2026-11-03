namespace CMakeWorkflow;

/// <summary>A reference to a previously declared node. It is not a running operation.</summary>
public readonly record struct WorkflowNodeRef(string Id);

/// <summary>A typed reference usable as the source of a run or profile node.</summary>
public readonly record struct BuildNodeRef(string Id)
{
    public static implicit operator WorkflowNodeRef(BuildNodeRef value) => new(value.Id);
}

/// <summary>
/// Builds schema-v2 workflow documents in memory. Never invokes CMake, the Host, or the filesystem.
/// The planner remains authoritative for target metadata, paths and execution admission.
/// </summary>
public sealed class WorkflowBuilder
{
    private readonly WorkflowDocument document = new() { SchemaVersion = 2 };
    private readonly Dictionary<string, WorkflowNode> nodes = new(StringComparer.Ordinal);

    public static WorkflowBuilder Create() => new();

    public WorkflowBuilder Context(string name, Action<WorkflowContextBuilder> configure)
    {
        Identifier(name, "context");
        ArgumentNullException.ThrowIfNull(configure);
        if (document.Contexts.ContainsKey(name))
            throw new ArgumentException($"Duplicate workflow context '{name}'.", nameof(name));
        var context = new WorkflowContext();
        configure(new WorkflowContextBuilder(context));
        if (string.IsNullOrWhiteSpace(context.BuildDirectory))
            throw new ArgumentException($"Context '{name}' requires a build directory.", nameof(configure));
        document.Contexts.Add(name, context);
        return this;
    }

    public WorkflowNodeRef Inspect(string id, string context, Action<WorkflowNodeBuilder>? configure = null) =>
        Add(id, "inspect", context, null, null, configure);

    public WorkflowNodeRef Configure(string id, string context, Action<WorkflowNodeBuilder>? configure = null) =>
        Add(id, "configure", context, null, null, configure);

    public BuildNodeRef BuildTarget(string id, string context, string target,
        Action<WorkflowNodeBuilder>? configure = null)
    {
        Target(target);
        Add(id, "build", context, target, null, configure);
        return new BuildNodeRef(id);
    }

    public WorkflowNodeRef Invoke(string id, string context, string target,
        Action<WorkflowNodeBuilder>? configure = null)
    {
        Target(target);
        return Add(id, "invoke", context, target, null, configure);
    }

    public WorkflowNodeRef Run(string id, BuildNodeRef from,
        Action<WorkflowNodeBuilder>? configure = null) =>
        Add(id, "run", null, null, RequireBuild(from), configure);

    public WorkflowNodeRef Profile(string id, BuildNodeRef from,
        Action<WorkflowNodeBuilder>? configure = null) =>
        Add(id, "profile", null, null, RequireBuild(from), configure);

    public WorkflowNodeRef CTest(string id, string context, BuildNodeRef afterBuild,
        Action<WorkflowNodeBuilder>? configure = null)
    {
        var source = RequireBuild(afterBuild);
        return Add(id, "ctest", context, null, null, node =>
        {
            node.DependsOn(new WorkflowNodeRef(source));
            configure?.Invoke(node);
        });
    }

    /// <summary>Returns a detached snapshot. Subsequent builder changes cannot mutate this document.</summary>
    public WorkflowDocument ToDocument() => new()
    {
        SchemaVersion = 2,
        Contexts = document.Contexts.ToDictionary(p => p.Key, p => Clone(p.Value), StringComparer.Ordinal),
        Nodes = document.Nodes.Select(Clone).ToList()
    };

    private WorkflowNodeRef Add(string id, string kind, string? context, string? target, string? from,
        Action<WorkflowNodeBuilder>? configure)
    {
        Identifier(id, "node");
        if (nodes.ContainsKey(id)) throw new ArgumentException($"Duplicate workflow node '{id}'.", nameof(id));
        if (context is not null && !document.Contexts.ContainsKey(context))
            throw new ArgumentException($"Unknown workflow context '{context}'.", nameof(context));
        var node = new WorkflowNode { Id = id, Kind = kind, Context = context, Target = target, From = from };
        configure?.Invoke(new WorkflowNodeBuilder(node, this));
        nodes.Add(id, node);
        document.Nodes.Add(node);
        return new WorkflowNodeRef(id);
    }

    internal void RequireNode(WorkflowNodeRef value)
    {
        if (!nodes.ContainsKey(value.Id))
            throw new ArgumentException($"Unknown or not-yet-declared workflow node '{value.Id}'.", nameof(value));
    }

    private string RequireBuild(BuildNodeRef value)
    {
        if (!nodes.TryGetValue(value.Id, out var node) || node.Kind != "build")
            throw new ArgumentException($"Run/profile source '{value.Id}' must be a declared build node.",
                nameof(value));
        return value.Id;
    }

    private static void Target(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Target is required.", nameof(value));
    }

    private static void Identifier(string value, string field)
    {
        if (string.IsNullOrEmpty(value) || value is "." or ".." ||
            !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new ArgumentException($"Invalid {field} identifier: '{value}'.", field);
    }

    private static WorkflowContext Clone(WorkflowContext source) => new()
    {
        BuildDirectory = source.BuildDirectory, Configuration = source.Configuration,
        ConfigurePreset = source.ConfigurePreset, SourceDirectory = source.SourceDirectory,
        Mode = source.Mode, Generator = source.Generator, ProvenancePolicy = source.ProvenancePolicy,
        Toolchain = source.Toolchain
    };

    private static WorkflowNode Clone(WorkflowNode source) => new()
    {
        Id = source.Id, Kind = source.Kind, Context = source.Context,
        Target = source.Target, From = source.From,
        DependsOn = [.. source.DependsOn], Arguments = [.. source.Arguments],
        WorkingDirectory = source.WorkingDirectory,
        Environment = new(source.Environment, StringComparer.Ordinal),
        Options = new(source.Options, StringComparer.Ordinal),
        HarnessDirectory = source.HarnessDirectory, TestRegex = source.TestRegex,
        LabelRegex = source.LabelRegex, Resources = [.. source.Resources],
        Exclusive = source.Exclusive, TimeoutSeconds = source.TimeoutSeconds,
        Inputs = source.Inputs.Select(x => new WorkflowArtifactInput
            { Path = x.Path, Sha256 = x.Sha256, From = x.From, Artifact = x.Artifact, Snapshot = x.Snapshot }).ToList(),
        Outputs = source.Outputs.Select(x => new WorkflowArtifactOutput
            { Name = x.Name, Path = x.Path, Root = x.Root, Fresh = x.Fresh }).ToList(),
        Constraints = source.Constraints is null
            ? null
            : new WorkflowExecutionConstraints
            {
                CpuSet = source.Constraints.CpuSet,
                Profiler = source.Constraints.Profiler is null
                    ? null
                    : new WorkflowProfilerRequirement
                    {
                        Backend = source.Constraints.Profiler.Backend,
                        Mode = source.Constraints.Profiler.Mode,
                        EventSource = source.Constraints.Profiler.EventSource
                    }
            }
    };
}

public sealed class WorkflowContextBuilder(WorkflowContext context)
{
    public WorkflowContextBuilder BuildDirectory(string path)
    {
        context.BuildDirectory = path;
        return this;
    }

    public WorkflowContextBuilder SourceDirectory(string path)
    {
        context.SourceDirectory = path;
        return this;
    }

    public WorkflowContextBuilder Configuration(string name)
    {
        context.Configuration = name;
        return this;
    }

    public WorkflowContextBuilder ConfigurePreset(string name)
    {
        context.ConfigurePreset = name;
        return this;
    }

    public WorkflowContextBuilder Generator(string name)
    {
        context.Generator = name;
        return this;
    }

    public WorkflowContextBuilder AdoptExisting()
    {
        context.Mode = "adopt";
        return this;
    }

    public WorkflowContextBuilder Managed()
    {
        context.Mode = "managed";
        return this;
    }

    public WorkflowContextBuilder Observe()
    {
        context.Mode = "observe";
        return this;
    }

    public WorkflowContextBuilder AllowUnverified()
    {
        context.ProvenancePolicy = "allowUnverified";
        return this;
    }

    public WorkflowContextBuilder Toolchain(WorkflowToolchainBinding binding)
    {
        context.Toolchain = binding;
        return this;
    }
}

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