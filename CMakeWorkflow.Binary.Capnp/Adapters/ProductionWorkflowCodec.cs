using Capnp.Managed;
using CMakeWorkflow.Contracts.Workflows;
using Binding = Capnp.Generated.ProductionWorkflow;

namespace CMakeWorkflow.Binary.Capnp;

/// <summary>Wire contract 1 for schema-1 and schema-2 production workflow values.</summary>
/// <remarks>The workflow planner remains responsible for domain validation and admission.</remarks>
public static partial class ProductionWorkflowCodec
{
    public static byte[] Encode(WorkflowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        CheckVersion(document.SchemaVersion);
        MessageBuilder builder = new(CapnpWire.ViewLimits);
        Binding.Manifest.Builder root = Binding.Manifest.InitRoot(builder);
        root.SetFormatVersion(1);
        root.SetSchemaVersion((uint)document.SchemaVersion);
        Binding.Manifest.ContextsListBuilder contexts = root.InitContexts(document.Contexts.Count);
        int contextIndex = 0;
        foreach ((string name, WorkflowContext context) in document.Contexts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            WriteContext(contexts[contextIndex++], name, context);
        }

        Binding.Manifest.NodesListBuilder nodes = root.InitNodes(document.Nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            WriteNode(nodes[i], document.Nodes[i]);
        }

        return builder.CopySegment();
    }

    private static void WriteContext(Binding.ContextEntry.Builder entry, string key, WorkflowContext value)
    {
        entry.SetKey(Required(key));
        entry.SetBuildDirectory(Required(value.BuildDirectory));
        entry.SetConfiguration(value.Configuration);
        entry.SetConfigurePreset(value.ConfigurePreset);
        entry.SetSourceDirectory(value.SourceDirectory);
        entry.SetMode(value.Mode);
        entry.SetGenerator(value.Generator);
        entry.SetProvenancePolicy(Required(value.ProvenancePolicy));
        entry.SetCompilerCacheProfile(value.CompilerCacheProfile);
        if (value.Toolchain is { } toolchain)
        {
            Binding.Toolchain.Builder wire = entry.InitToolchain();
            wire.SetCmakeCandidateId(toolchain.CMakeCandidateId);
            wire.SetNinjaCandidateId(toolchain.NinjaCandidateId);
            wire.SetCompilerCandidateId(toolchain.CompilerCandidateId);
            wire.SetSdkCandidateId(toolchain.SdkCandidateId);
            wire.SetEnvironmentProfileId(toolchain.EnvironmentProfileId);
            wire.SetGenerator(Required(toolchain.Generator));
            wire.SetHostArchitecture(Required(toolchain.HostArchitecture));
            wire.SetTargetArchitecture(Required(toolchain.TargetArchitecture));
            wire.SetEnvironmentLockSha256(toolchain.EnvironmentLockSha256);
            wire.SetVcpkgProjectSha256(toolchain.VcpkgProjectSha256);
        }

        if (value.BuildVariant is { } variant)
        {
            Binding.BuildVariant.Builder wire = entry.InitBuildVariant();
            wire.SetId(Required(variant.Id));
            wire.SetLockSha256(Required(variant.LockSha256));
            wire.SetKitLockSha256(variant.KitLockSha256);
        }
    }

    private static void WriteNode(Binding.Node.Builder entry, WorkflowNode value)
    {
        entry.SetId(Required(value.Id));
        entry.SetKind(value.Kind switch
        {
            "inspect" => Binding.Operation.Inspect,
            "configure" => Binding.Operation.Configure,
            "build" => Binding.Operation.Build,
            "invoke" => Binding.Operation.Invoke,
            "run" => Binding.Operation.Run,
            "profile" => Binding.Operation.Profile,
            "profile_snapshot" => Binding.Operation.ProfileSnapshot,
            "format_check" => Binding.Operation.FormatCheck,
            "ctest" => Binding.Operation.Ctest,
            _ => throw new InvalidDataException("Unsupported workflow operation.")
        });

        entry.SetContext(value.Context);
        entry.SetTarget(value.Target);
        entry.SetFrom(value.From);
        entry.SetSnapshotExecutable(value.SnapshotExecutable);
        entry.SetSnapshotSha256(value.SnapshotSha256);
        entry.SetWorkingDirectory(value.WorkingDirectory);
        entry.SetHarnessDirectory(value.HarnessDirectory);
        entry.SetTestRegex(value.TestRegex);
        entry.SetLabelRegex(value.LabelRegex);
        entry.SetExclusive(value.Exclusive);
        entry.SetActionCache(value.ActionCache);

        if (value.TimeoutSeconds is double seconds)
        {
            entry.InitTimeout().SetSeconds(seconds);
        }

        Binding.Node.DependsOnListBuilder dependsOn = entry.InitDependsOn(value.DependsOn.Count);
        for (int i = 0; i < dependsOn.Count; i++)
        {
            dependsOn.Set(i, Required(value.DependsOn[i]));
        }

        Binding.Node.ArgumentsListBuilder arguments = entry.InitArguments(value.Arguments.Count);
        for (int i = 0; i < arguments.Count; i++)
        {
            arguments.Set(i, Required(value.Arguments[i]));
        }

        Binding.Node.ResourcesListBuilder resources = entry.InitResources(value.Resources.Count);
        for (int i = 0; i < resources.Count; i++)
        {
            resources.Set(i, Required(value.Resources[i]));
        }

        Binding.Node.EnvironmentListBuilder environment = entry.InitEnvironment(value.Environment.Count);
        int index = 0;
        foreach ((string key, string item) in value.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            WriteProperty(environment[index++], key, item);
        }

        Binding.Node.OptionsListBuilder options = entry.InitOptions(value.Options.Count);
        index = 0;
        foreach ((string key, string item) in value.Options.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            WriteProperty(options[index++], key, item);
        }

        WriteArtifacts(entry, value);
        WriteBenchmark(entry, value);
        if (value.Constraints is { } requirements)
        {
            Binding.Constraints.Builder constraints = entry.InitConstraints();
            constraints.SetCpuSet(requirements.CpuSet);
            if (requirements.Profiler is { } profiler)
            {
                Binding.Profiler.Builder wire = constraints.InitProfiler();
                wire.SetBackend(Required(profiler.Backend));
                wire.SetMode(Required(profiler.Mode));
                wire.SetEventSource(profiler.EventSource);
            }
        }
    }

    private static void WriteProperty(Binding.Property.Builder entry, string key, string value)
    {
        entry.SetKey(Required(key));
        entry.SetValue(Required(value));
    }

    private static void WriteArtifacts(Binding.Node.Builder entry, WorkflowNode value)
    {
        Binding.Node.InputsListBuilder inputs = entry.InitInputs(value.Inputs.Count);
        for (int i = 0; i < inputs.Count; i++)
        {
            WorkflowArtifactInput source = value.Inputs[i];
            Binding.Input.Builder target = inputs[i];
            target.SetPath(source.Path);
            target.SetSha256(source.Sha256);
            target.SetFrom(source.From);
            target.SetArtifact(source.Artifact);
            target.SetSnapshot(source.Snapshot);
        }

        Binding.Node.OutputsListBuilder outputs = entry.InitOutputs(value.Outputs.Count);
        for (int i = 0; i < outputs.Count; i++)
        {
            WorkflowArtifactOutput source = value.Outputs[i];
            Binding.Output.Builder target = outputs[i];
            target.SetName(Required(source.Name));
            target.SetPath(Required(source.Path));
            target.SetRoot(Required(source.Root));
            target.SetFresh(source.Fresh);
        }
    }

    private static void WriteBenchmark(Binding.Node.Builder entry, WorkflowNode value)
    {
        if (value.Metrics is { } metrics)
        {
            Binding.Metrics.Builder target = entry.InitMetrics();
            target.SetSource(Required(metrics.Source));
            target.SetFormat(Required(metrics.Format));
            Binding.Metrics.FieldsListBuilder fields = target.InitFields(metrics.Fields.Count);
            for (int i = 0; i < fields.Count; i++)
            {
                BenchmarkMetricField field = metrics.Fields[i];
                Binding.MetricField.Builder wire = fields[i];
                wire.SetPath(Required(field.Path));
                wire.SetName(Required(field.Name));
                wire.SetUnit(Required(field.Unit));
                wire.SetDirection(Required(field.Direction));
            }
        }

        if (value.BenchmarkTrial is { } trial)
        {
            Binding.Trial.Builder target = entry.InitBenchmarkTrial();
            target.SetBaselineVariant(Required(trial.BaselineVariant));
            target.SetExecutionOrder(Required(trial.ExecutionOrder));
            Binding.Trial.AxesListBuilder axes = target.InitAxes(trial.Axes.Count);
            for (int i = 0; i < axes.Count; i++)
            {
                BenchmarkAxisCoordinate axis = trial.Axes[i];
                Binding.Axis.Builder wire = axes[i];
                wire.SetName(Required(axis.Name));
                wire.SetFlag(Required(axis.Flag));
                wire.SetValue(Required(axis.Value));
                wire.SetRole(axis.Role);
            }
        }
    }

    private static void CheckVersion(int version)
    {
        if (version is not (1 or 2))
        {
            throw new InvalidDataException("Unsupported production workflow schema version.");
        }
    }

    private static string Required(string? value) =>
        value ?? throw new InvalidDataException("Required workflow text is absent.");
}
