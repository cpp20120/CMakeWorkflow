using Capnp.Managed;
using Binding = Capnp.Generated.Workflow;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Binary.Capnp;

public static partial class WorkflowEncoding
{
    /// <summary>Validates the reachable wire tree and materializes the supported workflow fields.</summary>
    public static WorkflowDocument Decode(ReadOnlySpan<byte> segment)
    {
        CapnpWire.Validate(segment);
        return Materialize(new WorkflowView(segment));
    }

    public static WorkflowDocument Decode(MessageReader message)
    {
        CanonicalMessage.Validate(message);
        return Materialize(new WorkflowView(message));
    }

    internal static WorkflowDocument Materialize(WorkflowView view)
    {
        Binding.Workflow.ContextsListReader contexts = view.Contexts;
        WorkflowDocument result = new() { SchemaVersion = view.SchemaVersion };
        _ = result.Contexts.EnsureCapacity(contexts.Count);
        _ = result.Nodes.EnsureCapacity(view.NodeCount);
        for (int i = 0; i < contexts.Count; i++)
        {
            Binding.Context.Reader context = contexts[i];
            string id = Required(context.DecodeId());
            WorkflowContext value = new()
            {
                BuildDirectory = Required(context.DecodeBuildDirectory()),
                Configuration = context.DecodeConfiguration(),
                ConfigurePreset = context.DecodeConfigurePreset(),
                SourceDirectory = context.DecodeSourceDirectory(),
                Mode = context.DecodeMode(),
                Generator = context.DecodeGenerator(),
                ProvenancePolicy = context.DecodeProvenancePolicy() ?? "strict",
                CompilerCacheProfile = context.DecodeCompilerCacheProfile()
            };
            if (!result.Contexts.TryAdd(id, value))
            {
                throw new InvalidDataException("Duplicate context ID.");
            }
        }

        string[] ids = new string[view.NodeCount];
        HashSet<string> unique = new(ids.Length, StringComparer.Ordinal);
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = Required(view.GetNode(i).Struct.DecodeId());
            if (!unique.Add(ids[i]))
            {
                throw new InvalidDataException("Duplicate workflow node ID.");
            }
        }

        for (int i = 0; i < ids.Length; i++)
        {
            NodeView node = view.GetNode(i);
            result.Nodes.Add(ReadNode(in node, ids, i));
        }

        return result;
    }

    private static WorkflowNode ReadNode(in NodeView node, string[] ids, int index)
    {
        Binding.Node.Reader item = node.Struct;
        Binding.Node_Operation.Reader operation = item.Operation;
        Binding.Node_Operation.Variant kind = operation.Which;
        WorkflowNode result = new()
        {
            Id = ids[index],
            Kind = kind switch
            {
                Binding.Node_Operation.Variant.Configure => "configure",
                Binding.Node_Operation.Variant.Build => "build",
                Binding.Node_Operation.Variant.Run => "run",
                Binding.Node_Operation.Variant.Ctest => "ctest",
                _ => throw new InvalidDataException("Unknown workflow operation.")
            },
            Context = item.DecodeContext(),
            WorkingDirectory = item.DecodeWorkingDirectory(),
            Exclusive = item.Exclusive,
            TimeoutSeconds = item.HasTimeout ? item.TimeoutSeconds : null,
            Constraints = item.HasConstraints ? new() { CpuSet = item.DecodeCpuSet() } : null,
            ActionCache = item.ActionCache
        };

        if (kind == Binding.Node_Operation.Variant.Build)
        {
            result.Target = operation.DecodeBuild();
        }
        else if (kind == Binding.Node_Operation.Variant.Run)
        {
            result.From = node.ReadReference(operation.Run) is int from ? ids[from] : null;
        }
        else if (kind == Binding.Node_Operation.Variant.Ctest)
        {
            Binding.TestFilters.Reader filters = operation.Ctest;
            result.TestRegex = filters.DecodeTestRegex();
            result.LabelRegex = filters.DecodeLabelRegex();
        }

        DependencyListView dependencies = node.Dependencies;
        _ = result.DependsOn.EnsureCapacity(dependencies.Count);
        for (int i = 0; i < dependencies.Count; i++)
        {
            result.DependsOn.Add(ids[dependencies.GetNodeIndex(i)]);
        }

        Binding.Node.ArgumentsListReader arguments = item.Arguments;
        for (int i = 0; i < arguments.Count; i++)
        {
            result.Arguments.Add(Required(arguments.Decode(i)));
        }

        Binding.Node.ResourcesListReader resources = item.Resources;
        for (int i = 0; i < resources.Count; i++)
        {
            result.Resources.Add(Required(resources.Decode(i)));
        }

        Binding.Node.EnvironmentListReader environment = item.Environment;
        for (int i = 0; i < environment.Count; i++)
        {
            Binding.Property.Reader property = environment[i];
            if (!result.Environment.TryAdd(Required(property.DecodeKey()), Required(property.DecodeValue())))
            {
                throw new InvalidDataException("Duplicate environment property.");
            }
        }

        ReadArtifacts(in node, ids, result);
        return result;
    }

    private static void ReadArtifacts(in NodeView node, string[] ids, WorkflowNode result)
    {
        Binding.Node.InputsListReader inputs = node.Inputs;
        for (int i = 0; i < inputs.Count; i++)
        {
            Binding.Input.Reader input = inputs[i];
            result.Inputs.Add(new()
            {
                Path = input.DecodePath(),
                Sha256 = input.DecodeSha256(),
                From = node.ReadReference(input.From) is int index ? ids[index] : null,
                Artifact = input.DecodeArtifact(),
                Snapshot = input.Snapshot
            });
        }

        Binding.Node.OutputsListReader outputs = node.Struct.Outputs;
        for (int i = 0; i < outputs.Count; i++)
        {
            Binding.Output.Reader output = outputs[i];
            result.Outputs.Add(new()
            {
                Name = Required(output.DecodeName()),
                Path = Required(output.DecodePath()),
                Root = output.DecodeRoot() ?? "manifest",
                Fresh = output.Fresh
            });
        }
    }

    private static string Required(string? value) =>
        value ?? throw new InvalidDataException("Missing required workflow text.");
}
