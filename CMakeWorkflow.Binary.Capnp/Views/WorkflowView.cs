using Capnp.Managed;
using Binding = Capnp.Generated.Workflow;

namespace CMakeWorkflow.Binary.Capnp;

public enum OperationKind { Configure, Build, Run, Ctest }

/// <summary>A borrowed view of an immutable workflow message.</summary>
public readonly ref struct WorkflowView
{
    private readonly Binding.Workflow.NodesListReader nodes;

    public WorkflowView(ReadOnlySpan<byte> segment) : this(new MessageReader(segment, CapnpWire.ViewLimits))
    {
    }

    public WorkflowView(MessageReader message)
    {
        Binding.Workflow.Reader root = Binding.Workflow.ReadRoot(message);
        SchemaVersion = (int)root.SchemaVersion;
        if (SchemaVersion != 2)
        {
            throw new InvalidDataException("Expected workflow schema version 2.");
        }

        Contexts = root.Contexts;
        nodes = root.Nodes;
        if (nodes.Count == 0 || nodes.Count > CapnpWire.MaxCollection || Contexts.Count > CapnpWire.MaxCollection)
        {
            throw new InvalidDataException("Invalid workflow node or context count.");
        }
    }

    public int SchemaVersion { get; }
    public int NodeCount => nodes.Count;
    internal Binding.Workflow.ContextsListReader Contexts { get; }
    public NodeView GetNode(int index) => new(nodes[index], nodes.Count);

    public ReadOnlySpan<byte> GetNodeIdUtf8(int index) => nodes[index].IdUtf8;
}

/// <summary>One operation and its edges, accessed without materializing a workflow.</summary>
public readonly ref struct NodeView
{
    private readonly int nodeCount;

    internal NodeView(Binding.Node.Reader node, int nodeCount)
    {
        Struct = node;
        this.nodeCount = nodeCount;
    }

    public ReadOnlySpan<byte> IdUtf8 => Struct.IdUtf8;
    public ReadOnlySpan<byte> ContextUtf8 => Struct.ContextUtf8;
    public ReadOnlySpan<byte> BuildTargetUtf8 =>
        Kind == OperationKind.Build ? Struct.Operation.BuildUtf8 : default;

    public OperationKind Kind => DecodeKind(Struct.Operation.Which);

    private static OperationKind DecodeKind(Binding.Node_Operation.Variant kind) => kind switch
    {
        Binding.Node_Operation.Variant.Configure => OperationKind.Configure,
        Binding.Node_Operation.Variant.Build => OperationKind.Build,
        Binding.Node_Operation.Variant.Run => OperationKind.Run,
        Binding.Node_Operation.Variant.Ctest => OperationKind.Ctest,
        _ => throw new InvalidDataException("Unknown workflow operation.")
    };

    public int DependencyCount => Dependencies.Count;

    public int GetDependencyIndex(int index) => Dependencies.GetNodeIndex(index);

    public DependencyListView Dependencies => new(Struct.DependsOn, nodeCount);

    public int? RunSourceIndex => Kind == OperationKind.Run
        ? ReadReference(Struct.Operation.Run)
        : null;

    public int InputCount => Inputs.Count;
    public int? GetInputProducerIndex(int index) => ReadReference(Inputs[index].From);

    internal Binding.Node.Reader Struct { get; }
    internal Binding.Node.InputsListReader Inputs => Struct.Inputs;

    internal int? ReadReference(uint value) => value switch
    {
        0 => null,
        _ when value <= nodeCount => (int)value - 1,
        _ => throw new InvalidDataException("Producer outside the node list.")
    };
}


/// <summary>Direct access to one checked adjacency row, with range checks on each referenced node.</summary>
public readonly ref struct DependencyListView
{
    private readonly Binding.Node.DependsOnListReader indices;
    private readonly int nodeCount;

    internal DependencyListView(Binding.Node.DependsOnListReader indices, int nodeCount)
    {
        this.indices = indices;
        this.nodeCount = nodeCount;
    }

    public int Count => indices.Count;

    public int GetNodeIndex(int index)
    {
        uint value = indices[index];
        return value < nodeCount ? (int)value : throw new InvalidDataException("Dependency outside the node list.");
    }
}
