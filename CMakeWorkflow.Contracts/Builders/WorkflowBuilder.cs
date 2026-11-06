using CMakeWorkflow.Contracts.Workflows;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Contracts.Builders;
#pragma warning restore IDE0130

/// <summary>
/// Builds schema-v2 workflow documents in memory. Never invokes CMake, the Host, or the filesystem.
/// The planner remains authoritative for target metadata, paths and execution admission.
/// </summary>
public sealed class WorkflowBuilder
{
    private readonly WorkflowDocument document = new() { SchemaVersion = 2 };
    private readonly Dictionary<string, WorkflowNode> nodes = new(StringComparer.Ordinal);

    /// <summary>Creates an empty in-memory schema-v2 builder.</summary>
    /// <remarks>
    /// Only mutates this builder. The planner validates paths, targets, kind-specific fields and execution requirements later.
    /// </remarks>
    public static WorkflowBuilder Create() => new();

    /// <summary>Adds a named context after applying the callback; requires a build directory and a unique valid name.</summary>
    /// <remarks>
    /// Only mutates this builder. The planner validates paths, targets, kind-specific fields and execution requirements later.
    /// </remarks>
    public WorkflowBuilder Context(string name, Action<WorkflowContextBuilder> configure)
    {
        Identifier(name, "context");
        ArgumentNullException.ThrowIfNull(configure);
        if (document.Contexts.ContainsKey(name))
        {
            throw new ArgumentException($"Duplicate workflow context '{name}'.", nameof(name));
        }

        WorkflowContext context = new();
        configure(new WorkflowContextBuilder(context));
        if (string.IsNullOrWhiteSpace(context.BuildDirectory))
        {
            throw new ArgumentException($"Context '{name}' requires a build directory.", nameof(configure));
        }

        document.Contexts.Add(name, context);
        return this;
    }

    /// <summary>Adds a read-only inspection node for a previously declared context.</summary>
    /// <remarks>
    /// Only mutates this builder. The planner validates paths, targets, kind-specific fields and execution requirements later.
    /// </remarks>
    public WorkflowNodeRef Inspect(string id, string context, Action<WorkflowNodeBuilder>? configure = null) =>
        Add(id, "inspect", context, null, null, configure);

    /// <summary>Adds an explicit configuration node; planning requires a compatible managed context.</summary>
    /// <remarks>
    /// Only mutates this builder. The planner validates paths, targets, kind-specific fields and execution requirements later.
    /// </remarks>
    public WorkflowNodeRef Configure(string id, string context, Action<WorkflowNodeBuilder>? configure = null) =>
        Add(id, "configure", context, null, null, configure);

    /// <summary>Adds a native build node and returns a source reference for later run/profile nodes.</summary>
    /// <remarks>
    /// Only mutates this builder. The planner validates paths, targets, kind-specific fields and execution requirements later.
    /// </remarks>
    public BuildNodeRef BuildTarget(string id, string context, string target,
        Action<WorkflowNodeBuilder>? configure = null)
    {
        Target(target);
        _ = Add(id, "build", context, target, null, configure);
        return new BuildNodeRef(id);
    }

    /// <summary>Adds a native target invocation without claiming an executable result.</summary>
    /// <remarks>
    /// Only mutates this builder. The planner validates paths, targets, kind-specific fields and execution requirements later.
    /// </remarks>
    public WorkflowNodeRef Invoke(string id, string context, string target,
        Action<WorkflowNodeBuilder>? configure = null)
    {
        Target(target);
        return Add(id, "invoke", context, target, null, configure);
    }

    /// <summary>Adds execution of a previously declared build node; the source contributes a DAG dependency.</summary>
    /// <remarks>
    /// Only mutates this builder. The planner validates paths, targets, kind-specific fields and execution requirements later.
    /// </remarks>
    public WorkflowNodeRef Run(string id, BuildNodeRef from,
        Action<WorkflowNodeBuilder>? configure = null) =>
        Add(id, "run", null, null, RequireBuild(from), configure);

    /// <summary>Adds profiler execution from a previously declared build node; the source contributes a dependency.</summary>
    /// <remarks>
    /// Only mutates this builder. The planner validates paths, targets, kind-specific fields and execution requirements later.
    /// </remarks>
    public WorkflowNodeRef Profile(string id, BuildNodeRef from,
        Action<WorkflowNodeBuilder>? configure = null) =>
        Add(id, "profile", null, null, RequireBuild(from), configure);

    /// <summary>Adds a CTest node with an explicit dependency on a previously declared build node.</summary>
    /// <remarks>
    /// Only mutates this builder. The planner validates paths, targets, kind-specific fields and execution requirements later.
    /// </remarks>
    public WorkflowNodeRef CTest(string id, string context, BuildNodeRef afterBuild,
        Action<WorkflowNodeBuilder>? configure = null)
    {
        string source = RequireBuild(afterBuild);
        return Add(id, "ctest", context, null, null, node =>
        {
            _ = node.DependsOn(new WorkflowNodeRef(source));
            configure?.Invoke(node);
        });
    }

    /// <summary>Returns a detached snapshot. Subsequent builder changes cannot mutate this document.</summary>
    public WorkflowDocument ToDocument() => new()
    {
        SchemaVersion = 2,
        Contexts = document.Contexts.ToDictionary(p => p.Key, p => Clone(p.Value), StringComparer.Ordinal),
        Nodes = [.. document.Nodes.Select(Clone)]
    };

    private WorkflowNodeRef Add(string id, string kind, string? context, string? target, string? from,
        Action<WorkflowNodeBuilder>? configure)
    {
        Identifier(id, "node");
        if (nodes.ContainsKey(id))
        {
            throw new ArgumentException($"Duplicate workflow node '{id}'.", nameof(id));
        }

        if (context is not null && !document.Contexts.ContainsKey(context))
        {
            throw new ArgumentException($"Unknown workflow context '{context}'.", nameof(context));
        }

        WorkflowNode node = new() { Id = id, Kind = kind, Context = context, Target = target, From = from };
        configure?.Invoke(new WorkflowNodeBuilder(node, this));
        nodes.Add(id, node);
        document.Nodes.Add(node);
        return new WorkflowNodeRef(id);
    }

    internal void RequireNode(WorkflowNodeRef value)
    {
        if (!nodes.ContainsKey(value.Id))
        {
            throw new ArgumentException($"Unknown or not-yet-declared workflow node '{value.Id}'.", nameof(value));
        }
    }

    private string RequireBuild(BuildNodeRef value)
    {
        return !nodes.TryGetValue(value.Id, out WorkflowNode? node) || node.Kind != "build"
            ? throw new ArgumentException($"Run/profile source '{value.Id}' must be a declared build node.",
                nameof(value))
            : value.Id;
    }

    private static void Target(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Target is required.", nameof(value));
        }
    }

    private static void Identifier(string value, string field)
    {
        if (string.IsNullOrEmpty(value) || value is "." or ".." ||
            !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new ArgumentException($"Invalid {field} identifier: '{value}'.", field);
        }
    }

    private static WorkflowContext Clone(WorkflowContext source) => new()
    {
        BuildDirectory = source.BuildDirectory,
        Configuration = source.Configuration,
        ConfigurePreset = source.ConfigurePreset,
        SourceDirectory = source.SourceDirectory,
        Mode = source.Mode,
        Generator = source.Generator,
        ProvenancePolicy = source.ProvenancePolicy,
        Toolchain = source.Toolchain
    };

    private static WorkflowNode Clone(WorkflowNode source) => new()
    {
        Id = source.Id,
        Kind = source.Kind,
        Context = source.Context,
        Target = source.Target,
        From = source.From,
        DependsOn = [.. source.DependsOn],
        Arguments = [.. source.Arguments],
        WorkingDirectory = source.WorkingDirectory,
        Environment = new(source.Environment, StringComparer.Ordinal),
        Options = new(source.Options, StringComparer.Ordinal),
        HarnessDirectory = source.HarnessDirectory,
        TestRegex = source.TestRegex,
        LabelRegex = source.LabelRegex,
        Resources = [.. source.Resources],
        Exclusive = source.Exclusive,
        TimeoutSeconds = source.TimeoutSeconds,
        Inputs = [.. source.Inputs.Select(x => new WorkflowArtifactInput
        { Path = x.Path, Sha256 = x.Sha256, From = x.From, Artifact = x.Artifact, Snapshot = x.Snapshot })],
        Outputs = [.. source.Outputs.Select(x => new WorkflowArtifactOutput
        { Name = x.Name, Path = x.Path, Root = x.Root, Fresh = x.Fresh })],
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
