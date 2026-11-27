using Capnp.Managed;
using Binding = Capnp.Generated.Workflow;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Binary.Capnp;

/// <summary>Schema-v2 workflow manifest prototype using the managed Cap’n Proto runtime.</summary>
public static partial class WorkflowEncoding
{
    /// <summary>Normalizes domain values and builds an unframed segment suitable for canonicalization.</summary>
    public static byte[] Encode(WorkflowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 2 || document.Nodes.Count == 0)
        {
            throw new ArgumentException("Expected a nonempty schema-v2 workflow.", nameof(document));
        }

        Limit(document.Nodes.Count);
        Limit(document.Contexts.Count);
        Dictionary<string, int> indices = new(StringComparer.Ordinal);
        for (int i = 0; i < document.Nodes.Count; i++)
        {
            indices.Add(document.Nodes[i].Id, i);
        }

        // Composite lists reserve all elements up front. Include both lists before arena growth starts.
        StructLayout rootLayout = Binding.Workflow.Layout;
        StructLayout nodeLayout = Binding.Node.Layout;
        StructLayout contextLayout = Binding.Context.Layout;
        int structuralWords = 3 + rootLayout.DataWords + rootLayout.PointerWords
            + (document.Nodes.Count * (nodeLayout.DataWords + nodeLayout.PointerWords))
            + (document.Contexts.Count * (contextLayout.DataWords + contextLayout.PointerWords));
        MessageBuilder builder = new(CapnpWire.ViewLimits, structuralWords * sizeof(ulong));
        Binding.Workflow.Builder root = Binding.Workflow.InitRoot(builder);
        root.SetSchemaVersion((uint)document.SchemaVersion);

        // Deliberately build nodes before contexts to keep wire canonicalization observable.
        Binding.Workflow.NodesListBuilder nodes = root.InitNodes(document.Nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            WriteNode(nodes[i], document.Nodes[i], indices);
        }

        if (document.Contexts.Count > 0)
        {
            Binding.Workflow.ContextsListBuilder contexts = root.InitContexts(document.Contexts.Count);
            int i = 0;
            foreach ((string id, WorkflowContext context) in document.Contexts.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (context.Toolchain is not null || context.BuildVariant is not null)
                {
                    throw new NotSupportedException("Toolchain and Project SDK bindings are outside the prototype.");
                }

                Binding.Context.Builder item = contexts[i++];
                item.SetId(id);
                item.SetBuildDirectory(context.BuildDirectory);
                item.SetConfiguration(context.Configuration);
                item.SetConfigurePreset(context.ConfigurePreset);
                item.SetSourceDirectory(context.SourceDirectory);
                item.SetMode(context.Mode);
                item.SetGenerator(context.Generator);
                item.SetProvenancePolicy(context.ProvenancePolicy);
                item.SetCompilerCacheProfile(context.CompilerCacheProfile);
            }
        }

        return builder.CopySegment();
    }

    private static void WriteNode(Binding.Node.Builder item, WorkflowNode node, Dictionary<string, int> indices)
    {
        if (node.Metrics is not null || node.BenchmarkTrial is not null || node.SnapshotExecutable is not null
            || node.SnapshotSha256 is not null || node.HarnessDirectory is not null || node.Options.Count != 0
            || node.Constraints?.Profiler is not null)
        {
            throw new NotSupportedException("The node uses fields outside the prototype.");
        }

        OperationKind kind = node.Kind switch
        {
            "configure" => OperationKind.Configure,
            "build" => OperationKind.Build,
            "run" => OperationKind.Run,
            "ctest" => OperationKind.Ctest,
            _ => throw new NotSupportedException($"Unsupported workflow operation '{node.Kind}'.")
        };
        if ((kind != OperationKind.Build && node.Target is not null)
            || (kind != OperationKind.Run && node.From is not null)
            || (kind != OperationKind.Ctest && (node.TestRegex is not null || node.LabelRegex is not null)))
        {
            throw new ArgumentException("The node contains fields for a different operation.", nameof(node));
        }

        item.SetExclusive(node.Exclusive);
        item.SetHasTimeout(node.TimeoutSeconds.HasValue);
        item.SetTimeoutSeconds(node.TimeoutSeconds.GetValueOrDefault());
        item.SetHasConstraints(node.Constraints is not null);
        item.SetActionCache(node.ActionCache);
        item.SetId(node.Id);
        item.SetContext(node.Context);
        Limit(node.DependsOn.Count);
        if (node.DependsOn.Count > 0)
        {
            int[] dependencies = [.. node.DependsOn.Select(id => Index(indices, id)).Distinct().Order()];
            Binding.Node.DependsOnListBuilder edges = item.InitDependsOn(dependencies.Length);
            for (int i = 0; i < dependencies.Length; i++)
            {
                edges.Set(i, (uint)dependencies[i]);
            }
        }

        Limit(node.Arguments.Count);
        if (node.Arguments.Count > 0)
        {
            Binding.Node.ArgumentsListBuilder arguments = item.InitArguments(node.Arguments.Count);
            for (int i = 0; i < arguments.Count; i++)
            {
                arguments.Set(i, node.Arguments[i]);
            }
        }

        item.SetWorkingDirectory(node.WorkingDirectory);
        Limit(node.Environment.Count);
        if (node.Environment.Count > 0)
        {
            Binding.Node.EnvironmentListBuilder properties = item.InitEnvironment(node.Environment.Count);
            int i = 0;
            foreach ((string key, string value) in node.Environment.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Binding.Property.Builder property = properties[i++];
                property.SetKey(key);
                property.SetValue(value);
            }
        }

        Limit(node.Resources.Count);
        if (node.Resources.Count > 0)
        {
            Binding.Node.ResourcesListBuilder resources = item.InitResources(node.Resources.Count);
            for (int i = 0; i < resources.Count; i++)
            {
                resources.Set(i, node.Resources[i]);
            }
        }

        WriteArtifacts(item, node, indices);
        item.SetCpuSet(node.Constraints?.CpuSet);
        Binding.Node_Operation.Builder operation = item.InitOperation();
        switch (kind)
        {
            case OperationKind.Configure:
                operation.SelectConfigure();
                break;
            case OperationKind.Build:
                operation.SetBuild(node.Target);
                break;
            case OperationKind.Run:
                operation.SetRun(Reference(indices, node.From));
                break;
            case OperationKind.Ctest:
                if (node.TestRegex is null && node.LabelRegex is null)
                {
                    operation.ClearCtest();
                }
                else
                {
                    Binding.TestFilters.Builder filters = operation.InitCtest();
                    filters.SetTestRegex(node.TestRegex);
                    filters.SetLabelRegex(node.LabelRegex);
                }

                break;
            default:
                throw new InvalidOperationException("Unknown workflow operation.");
        }
    }

    private static void WriteArtifacts(Binding.Node.Builder item, WorkflowNode node,
        Dictionary<string, int> indices)
    {
        Limit(node.Inputs.Count);
        if (node.Inputs.Count > 0)
        {
            Binding.Node.InputsListBuilder inputs = item.InitInputs(node.Inputs.Count);
            for (int i = 0; i < inputs.Count; i++)
            {
                Binding.Input.Builder target = inputs[i];
                WorkflowArtifactInput input = node.Inputs[i];
                target.SetFrom(Reference(indices, input.From));
                target.SetSnapshot(input.Snapshot);
                target.SetPath(input.Path);
                target.SetSha256(input.Sha256);
                target.SetArtifact(input.Artifact);
            }
        }

        Limit(node.Outputs.Count);
        if (node.Outputs.Count > 0)
        {
            Binding.Node.OutputsListBuilder outputs = item.InitOutputs(node.Outputs.Count);
            for (int i = 0; i < outputs.Count; i++)
            {
                Binding.Output.Builder target = outputs[i];
                WorkflowArtifactOutput output = node.Outputs[i];
                target.SetFresh(output.Fresh);
                target.SetName(output.Name);
                target.SetPath(output.Path);
                target.SetRoot(output.Root);
            }
        }
    }

    private static int Index(Dictionary<string, int> indices, string id) =>
        indices.TryGetValue(id, out int index) ? index : throw new ArgumentException($"Unknown workflow node '{id}'.");

    private static uint Reference(Dictionary<string, int> indices, string? id) =>
        id is null ? 0 : (uint)(Index(indices, id) + 1);

    private static void Limit(int count)
    {
        if (count > CapnpWire.MaxCollection)
        {
            throw new ArgumentException("Collection exceeds the workflow codec limit.");
        }
    }
}
