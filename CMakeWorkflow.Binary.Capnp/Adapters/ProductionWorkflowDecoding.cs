using Capnp.Managed;
using CMakeWorkflow.Contracts.Workflows;
using Binding = Capnp.Generated.ProductionWorkflow;

namespace CMakeWorkflow.Binary.Capnp;

public static partial class ProductionWorkflowCodec
{
    /// <summary>Checks wire structure and materializes known production workflow values.</summary>
    public static WorkflowDocument Decode(ReadOnlySpan<byte> segment)
    {
        MessageReader message = new(segment, CapnpWire.ViewLimits);
        CanonicalMessage.Validate(message);
        return Materialize(message);
    }

    internal static WorkflowDocument Materialize(MessageReader message)
    {
        Binding.Manifest.Reader root = Binding.Manifest.ReadRoot(message);
        if (root.FormatVersion != 1)
        {
            throw new InvalidDataException("Unsupported production workflow wire version.");
        }

        int schemaVersion = unchecked((int)root.SchemaVersion);
        CheckVersion(schemaVersion);
        WorkflowDocument result = new() { SchemaVersion = schemaVersion };
        Binding.Manifest.ContextsListReader contexts = root.Contexts;
        _ = result.Contexts.EnsureCapacity(contexts.Count);
        foreach (Binding.ContextEntry.Reader context in contexts)
        {
            if (!result.Contexts.TryAdd(Required(context.DecodeKey()), ReadContext(context)))
            {
                throw new InvalidDataException("Duplicate workflow context.");
            }
        }

        Binding.Manifest.NodesListReader nodes = root.Nodes;
        _ = result.Nodes.EnsureCapacity(nodes.Count);
        foreach (Binding.Node.Reader node in nodes)
        {
            result.Nodes.Add(ReadNode(node));
        }

        return result;
    }

    private static WorkflowContext ReadContext(Binding.ContextEntry.Reader entry)
    {
        WorkflowContext value = new()
        {
            BuildDirectory = Required(entry.DecodeBuildDirectory()),
            Configuration = entry.DecodeConfiguration(),
            ConfigurePreset = entry.DecodeConfigurePreset(),
            SourceDirectory = entry.DecodeSourceDirectory(),
            Mode = entry.DecodeMode(),
            Generator = entry.DecodeGenerator(),
            ProvenancePolicy = Required(entry.DecodeProvenancePolicy()),
            CompilerCacheProfile = entry.DecodeCompilerCacheProfile()
        };

        if (entry.HasToolchain)
        {
            Binding.Toolchain.Reader wire = entry.Toolchain;
            value.Toolchain = new(wire.DecodeCmakeCandidateId(), wire.DecodeNinjaCandidateId(),
                wire.DecodeCompilerCandidateId(), wire.DecodeSdkCandidateId(), wire.DecodeEnvironmentProfileId(),
                Required(wire.DecodeGenerator()), Required(wire.DecodeHostArchitecture()), Required(wire.DecodeTargetArchitecture()))
            {
                EnvironmentLockSha256 = wire.DecodeEnvironmentLockSha256(),
                VcpkgProjectSha256 = wire.DecodeVcpkgProjectSha256()
            };
        }

        if (entry.HasBuildVariant)
        {
            Binding.BuildVariant.Reader wire = entry.BuildVariant;
            value.BuildVariant = new(Required(wire.DecodeId()), Required(wire.DecodeLockSha256()))
            {
                KitLockSha256 = wire.DecodeKitLockSha256()
            };
        }

        return value;
    }

    private static WorkflowNode ReadNode(Binding.Node.Reader entry)
    {
        Binding.Node_Timeout.Reader timeout = entry.Timeout;
        WorkflowNode node = new()
        {
            Id = Required(entry.DecodeId()),
            Kind = entry.Kind switch
            {
                Binding.Operation.Inspect => "inspect",
                Binding.Operation.Configure => "configure",
                Binding.Operation.Build => "build",
                Binding.Operation.Invoke => "invoke",
                Binding.Operation.Run => "run",
                Binding.Operation.Profile => "profile",
                Binding.Operation.ProfileSnapshot => "profile_snapshot",
                Binding.Operation.FormatCheck => "format_check",
                Binding.Operation.Ctest => "ctest",
                Binding.Operation.Unspecified => throw new InvalidDataException("Missing production workflow operation."),
                _ => throw new InvalidDataException("Unknown production workflow operation.")
            },
            Context = entry.DecodeContext(),
            Target = entry.DecodeTarget(),
            From = entry.DecodeFrom(),
            SnapshotExecutable = entry.DecodeSnapshotExecutable(),
            SnapshotSha256 = entry.DecodeSnapshotSha256(),
            WorkingDirectory = entry.DecodeWorkingDirectory(),
            HarnessDirectory = entry.DecodeHarnessDirectory(),
            TestRegex = entry.DecodeTestRegex(),
            LabelRegex = entry.DecodeLabelRegex(),
            Exclusive = entry.Exclusive,
            ActionCache = entry.ActionCache,
            TimeoutSeconds = timeout.Which switch
            {
                Binding.Node_Timeout.Variant.NoTimeout => null,
                Binding.Node_Timeout.Variant.Seconds => timeout.Seconds,
                _ => throw new InvalidDataException("Unknown timeout union variant.")
            },
            Metrics = entry.HasMetrics ? ReadMetrics(entry.Metrics) : null,
            BenchmarkTrial = entry.HasBenchmarkTrial ? ReadTrial(entry.BenchmarkTrial) : null,
            Constraints = entry.HasConstraints ? ReadConstraints(entry.Constraints) : null
        };

        Binding.Node.DependsOnListReader dependsOn = entry.DependsOn;
        for (int i = 0; i < dependsOn.Count; i++)
        {
            node.DependsOn.Add(Required(dependsOn.Decode(i)));
        }

        Binding.Node.ArgumentsListReader arguments = entry.Arguments;
        for (int i = 0; i < arguments.Count; i++)
        {
            node.Arguments.Add(Required(arguments.Decode(i)));
        }

        Binding.Node.ResourcesListReader resources = entry.Resources;
        for (int i = 0; i < resources.Count; i++)
        {
            node.Resources.Add(Required(resources.Decode(i)));
        }

        foreach (Binding.Property.Reader pair in entry.Environment)
        {
            ReadProperty(node.Environment, pair);
        }

        foreach (Binding.Property.Reader pair in entry.Options)
        {
            ReadProperty(node.Options, pair);
        }

        ReadArtifacts(entry, node);
        return node;
    }

    private static void ReadProperty(Dictionary<string, string> destination, Binding.Property.Reader entry)
    {
        if (!destination.TryAdd(Required(entry.DecodeKey()), Required(entry.DecodeValue())))
        {
            throw new InvalidDataException("Duplicate workflow map key.");
        }
    }

    private static void ReadArtifacts(Binding.Node.Reader entry, WorkflowNode node)
    {
        foreach (Binding.Input.Reader input in entry.Inputs)
        {
            node.Inputs.Add(new()
            {
                Path = input.DecodePath(),
                Sha256 = input.DecodeSha256(),
                From = input.DecodeFrom(),
                Artifact = input.DecodeArtifact(),
                Snapshot = input.Snapshot
            });
        }

        foreach (Binding.Output.Reader output in entry.Outputs)
        {
            node.Outputs.Add(new()
            {
                Name = Required(output.DecodeName()),
                Path = Required(output.DecodePath()),
                Root = Required(output.DecodeRoot()),
                Fresh = output.Fresh
            });
        }
    }

    private static WorkflowExecutionConstraints ReadConstraints(Binding.Constraints.Reader entry)
    {
        WorkflowExecutionConstraints value = new() { CpuSet = entry.DecodeCpuSet() };
        if (entry.HasProfiler)
        {
            Binding.Profiler.Reader wire = entry.Profiler;
            value.Profiler = new()
            {
                Backend = Required(wire.DecodeBackend()),
                Mode = Required(wire.DecodeMode()),
                EventSource = wire.DecodeEventSource()
            };
        }

        return value;
    }

    private static BenchmarkMetrics ReadMetrics(Binding.Metrics.Reader entry)
    {
        Binding.Metrics.FieldsListReader fields = entry.Fields;
        BenchmarkMetricField[] values = new BenchmarkMetricField[fields.Count];
        for (int i = 0; i < values.Length; i++)
        {
            Binding.MetricField.Reader wire = fields[i];
            values[i] = new(Required(wire.DecodePath()), Required(wire.DecodeName()),
                Required(wire.DecodeUnit()), Required(wire.DecodeDirection()));
        }

        return new(Required(entry.DecodeSource()), Required(entry.DecodeFormat()), values);
    }

    private static BenchmarkTrial ReadTrial(Binding.Trial.Reader entry)
    {
        Binding.Trial.AxesListReader axes = entry.Axes;
        BenchmarkAxisCoordinate[] values = new BenchmarkAxisCoordinate[axes.Count];
        for (int i = 0; i < values.Length; i++)
        {
            Binding.Axis.Reader wire = axes[i];
            values[i] = new(Required(wire.DecodeName()), Required(wire.DecodeFlag()),
                Required(wire.DecodeValue()), wire.DecodeRole());
        }

        return new(Required(entry.DecodeBaselineVariant()), Required(entry.DecodeExecutionOrder()), values);
    }
}
