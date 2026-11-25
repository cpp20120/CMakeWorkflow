using System.Text.Json;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Scripting;

/// <summary>Creates immutable workflow declarations without registering or executing them.</summary>
public sealed class Workflow
{
    private readonly string? sourceDirectory;

    private Workflow(string name, string? sourceDirectory)
    {
        (Name, this.sourceDirectory) = (name, sourceDirectory);
    }

    public string Name { get; }

    /// <summary>The optional default source path is interpreted relative to the manifest on the Host.</summary>
    public static Workflow Define(string name, string? sourceDirectory = null)
    {
        Identifier(name, nameof(name));
        return new(name, sourceDirectory);
    }

    /// <summary>Creates a deterministic namespace for one reusable fragment instance.</summary>
    public WorkflowScope Scope(string instance) => new(this, WorkflowScope.Segment(instance));

    public ContextRef Context(string id, string buildDirectory, Action<WorkflowContext>? options = null)
    {
        Identifier(id, nameof(id));
        WorkflowContext context = new() { BuildDirectory = buildDirectory, SourceDirectory = sourceDirectory };
        options?.Invoke(context);
        return string.IsNullOrWhiteSpace(context.BuildDirectory)
            ? throw new ArgumentException("A workflow context requires a build directory.", nameof(buildDirectory))
            : new(this, id, JsonSerializer.SerializeToElement(context, WorkflowDefinitionJson.Default.WorkflowContext));
    }

    public NodeRef Inspect(string id, ContextRef context, IEnumerable<NodeRef>? after = null, Action<WorkflowNode>? options = null) =>
        Declare(new() { Id = id, Kind = "inspect", Context = ContextId(context) }, context, null, after, options);

    public NodeRef Configure(string id, ContextRef context, IEnumerable<NodeRef>? after = null, Action<WorkflowNode>? options = null) =>
        Declare(new() { Id = id, Kind = "configure", Context = ContextId(context) }, context, null, after, options);

    public TargetBuildRef Build(string id, ContextRef context, string target, IEnumerable<NodeRef>? after = null,
        Action<WorkflowNode>? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        (NodeRef[] dependencies, JsonElement definition) = Freeze(new() { Id = id, Kind = "build", Context = ContextId(context), Target = target },
            null, after, options);
        return new(this, id, context, dependencies, definition);
    }

    public NodeRef Invoke(string id, ContextRef context, string target, IEnumerable<NodeRef>? after = null,
        Action<WorkflowNode>? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return Declare(new() { Id = id, Kind = "invoke", Context = ContextId(context), Target = target }, context, null, after, options);
    }

    public NodeRef CTest(string id, ContextRef context, IEnumerable<NodeRef>? after = null, Action<WorkflowNode>? options = null) =>
        Declare(new() { Id = id, Kind = "ctest", Context = ContextId(context) }, context, null, after, options);

    public NodeRef Run(string id, TargetBuildRef from, IEnumerable<NodeRef>? after = null,
        Action<WorkflowNode>? options = null, BenchmarkMetrics? metrics = null, BenchmarkTrial? trial = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        return Declare(new()
        {
            Id = id,
            Kind = "run",
            From = BuildId(from),
            Metrics = metrics,
            BenchmarkTrial = trial
        }, null, from, after, options);
    }

    public NodeRef Profile(string id, TargetBuildRef from, IEnumerable<NodeRef>? after = null, Action<WorkflowNode>? options = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        return Declare(new() { Id = id, Kind = "profile", From = BuildId(from) }, null, from, after, options);
    }

    public NodeRef ProfileSnapshot(string id, string executable, string sha256,
        IEnumerable<NodeRef>? after = null, Action<WorkflowNode>? options = null) =>
        Declare(new() { Id = id, Kind = "profile_snapshot", SnapshotExecutable = executable, SnapshotSha256 = sha256 },
            null, null, after, options);

    /// <summary>Freezes the supplied roots and their transitive prerequisites into the standard schema-v2 manifest.</summary>
    public WorkflowManifest Compose(IEnumerable<NodeRef> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        WorkflowDocument definition = new() { SchemaVersion = 2 };
        Dictionary<string, NodeRef> nodes = new(StringComparer.Ordinal);
        Dictionary<string, ContextRef> contexts = new(StringComparer.Ordinal);
        Queue<NodeRef> pending = new();
        foreach (NodeRef root in roots)
        {
            RequireOwned(root);
            pending.Enqueue(root);
        }
        if (pending.Count == 0)
        {
            throw new ArgumentException("A workflow requires at least one root.", nameof(roots));
        }

        while (pending.TryDequeue(out NodeRef? node))
        {
            if (!nodes.TryAdd(node.Id, node))
            {
                if (nodes[node.Id] != node)
                {
                    throw new ArgumentException($"Duplicate node identity '{node.Id}'.", nameof(roots));
                }
                continue;
            }
            definition.Nodes.Add(node.Definition.Deserialize(WorkflowDefinitionJson.Default.WorkflowNode)!);
            if (node.Context is { } context)
            {
                if (contexts.TryAdd(context.Id, context))
                {
                    definition.Contexts.Add(context.Id, context.Definition.Deserialize(WorkflowDefinitionJson.Default.WorkflowContext)!);
                }
                else if (contexts[context.Id] != context)
                {
                    throw new ArgumentException($"Duplicate context identity '{context.Id}'.", nameof(roots));
                }
            }
            foreach (NodeRef dependency in node.Prerequisites)
            {
                pending.Enqueue(dependency);
            }
        }
        return new(Name, JsonSerializer.SerializeToUtf8Bytes(definition, WorkflowDefinitionJson.Default.WorkflowDocument));
    }

    private NodeRef Declare(WorkflowNode node, ContextRef? context, TargetBuildRef? source,
        IEnumerable<NodeRef>? after, Action<WorkflowNode>? options)
    {
        (NodeRef[] dependencies, JsonElement definition) = Freeze(node, source, after, options);
        return new(this, node.Id, context, dependencies, definition);
    }

    private (NodeRef[] Dependencies, JsonElement Definition) Freeze(WorkflowNode node, TargetBuildRef? source,
        IEnumerable<NodeRef>? after, Action<WorkflowNode>? options)
    {
        Identifier(node.Id, "id");
        NodeRef[] dependencies = after is null ? [] : [.. after.Distinct()];
        foreach (NodeRef dependency in dependencies)
        {
            RequireOwned(dependency);
        }

        string[] ids = [.. dependencies.Select(dependency => dependency.Id)];
        node.DependsOn = [.. ids];
        (string Id, string Kind, string? Context, string? Target, string? From, string? SnapshotExecutable, string? SnapshotSha256) identity = (node.Id, node.Kind, node.Context, node.Target, node.From, node.SnapshotExecutable, node.SnapshotSha256);
        options?.Invoke(node);
        if ((node.Id, node.Kind, node.Context, node.Target, node.From, node.SnapshotExecutable, node.SnapshotSha256) != identity ||
            node.DependsOn is null || !node.DependsOn.SequenceEqual(ids, StringComparer.Ordinal))
        {
            throw new ArgumentException("Set node identity and dependencies through the authoring methods.", nameof(options));
        }

        if (node.Inputs is null || node.Inputs.Any(input => input is null ||
            (input.From is { } producer && producer != identity.From && !ids.Contains(producer, StringComparer.Ordinal))))
        {
            throw new ArgumentException("An artifact producer must also be supplied as a typed prerequisite.", nameof(options));
        }

        NodeRef[] closure = source is null ? dependencies : [.. dependencies, source];
        JsonElement frozen = JsonSerializer.SerializeToElement(node, WorkflowDefinitionJson.Default.WorkflowNode);
        return (closure, frozen);
    }

    private string ContextId(ContextRef context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Owner == this ? context.Id : throw new ArgumentException("A context belongs to a different workflow.", nameof(context));
    }

    private string BuildId(TargetBuildRef source)
    {
        RequireOwned(source);
        return source.Id;
    }

    private void RequireOwned(NodeRef node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Owner != this)
        {
            throw new ArgumentException("A node belongs to a different workflow.", nameof(node));
        }
    }

    private static void Identifier(string value, string paramName)
    {
        if (string.IsNullOrEmpty(value) || value is "." or ".." ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException("Expected an identifier using ASCII letters, digits, dots, underscores or hyphens.", paramName);
        }
    }
}
