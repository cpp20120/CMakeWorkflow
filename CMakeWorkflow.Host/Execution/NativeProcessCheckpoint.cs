using Capnp.Managed;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.Contracts.Workflows;
using Report = Capnp.Generated.ExecutionReport;
using Wire = Capnp.Generated.ProcessEvidence;

namespace CMakeWorkflow.Host.Execution;

internal sealed record ProcessCheckpointBinding(string WorkspaceId, string OperationId, string RequestDigest, string RunDirectory);
internal sealed record NativeProcessCheckpoint(ProcessCheckpointBinding Binding, long Generation, long EventCursor, ProcessOperationReport Report);

internal static class NativeProcessCheckpointCodec
{
    private static readonly MessageLimits Limits = MessageLimits.Default with
    {
        MaxMessageBytes = 16 * 1024 * 1024,
        MaxSegmentBytes = 16 * 1024 * 1024,
        MaxSegments = 1,
        MaxDataBytes = 4 * 1024 * 1024,
        MaxTraversalWords = 4 * 1024 * 1024
    };

    internal static byte[] Encode(NativeProcessCheckpoint checkpoint)
    {
        MessageBuilder message = new(Limits);
        Wire.ProcessCheckpoint.Builder root = Wire.ProcessCheckpoint.InitRoot(message);
        root.SetVersion(1);
        root.SetWorkspaceId(checkpoint.Binding.WorkspaceId);
        root.SetOperationId(checkpoint.Binding.OperationId);
        root.SetRequestDigest(checkpoint.Binding.RequestDigest);
        root.SetRunDirectory(checkpoint.Binding.RunDirectory);
        root.SetGeneration(checkpoint.Generation);
        root.SetEventCursor(checkpoint.EventCursor);
        ProcessOperationReport report = checkpoint.Report;
        root.SetSnapshotSha256(report.SnapshotSha256);
        root.SetStatus(report.Status);
        root.SetError(report.Error);
        root.SetRecoveryPhase(report.RecoveryPhase);
        root.SetResumeCount(report.ResumeCount);
        if (report.FinishedAt is { } finished)
        {
            WriteTime(root.InitFinishedAt(), finished);
        }
        if (report.Recovery is { } recovery)
        {
            Wire.Recovery.Builder trace = root.InitRecovery();
            trace.SetNote(recovery.Note);
            WriteTime(trace.InitConfirmedAt(), recovery.ConfirmedAt);
            trace.SetPreviousCheckpoint(recovery.PreviousCheckpoint);
            trace.SetDispatchCheckpoint(recovery.DispatchCheckpoint);
        }
        Wire.ProcessCheckpoint.NodesListBuilder nodes = root.InitNodes(report.Nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            WriteNode(nodes[i], report.Nodes[i]);
        }
        return CapnpWire.Frame(CapnpWire.Canonicalize(message.CopySegment()));
    }

    internal static NativeProcessCheckpoint Decode(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> segment = CapnpWire.Unframe(bytes);
        CapnpWire.Validate(segment);
        Wire.ProcessCheckpoint.Reader root = Wire.ProcessCheckpoint.ReadRoot(new MessageReader(segment, Limits));
        if (root.Version != 1 || root.Generation < 1 || root.EventCursor < 0 || root.Nodes.Count is < 1 or > 128)
        {
            throw new InvalidDataException("Invalid native process checkpoint.");
        }

        List<ProcessNodeReport> nodes = new(root.Nodes.Count);
        foreach (Wire.ProcessNode.Reader node in root.Nodes)
        {
            nodes.Add(ReadNode(node));
        }
        ProcessOperationReport report = new()
        {
            SnapshotSha256 = Required(root.DecodeSnapshotSha256()),
            Status = Required(root.DecodeStatus()),
            Error = root.DecodeError(),
            RecoveryPhase = root.DecodeRecoveryPhase(),
            ResumeCount = root.ResumeCount,
            FinishedAt = root.HasFinishedAt ? Time(root.FinishedAt) : null,
            Nodes = nodes
        };
        if (root.HasRecovery)
        {
            Wire.Recovery.Reader audit = root.Recovery;
            report.Recovery = new(Required(audit.DecodeNote()), Time(audit.ConfirmedAt),
                audit.PreviousCheckpoint.ToArray(), audit.DispatchCheckpoint.ToArray());
        }
        _ = ProcessCheckpointEvidenceCodec.Validate(report);
        return new(new(Required(root.DecodeWorkspaceId()), Required(root.DecodeOperationId()),
            Required(root.DecodeRequestDigest()), Required(root.DecodeRunDirectory())), root.Generation, root.EventCursor, report);
    }

    internal static bool EqualNodes(ProcessNodeReport lhs, ProcessNodeReport rhs) =>
        lhs.Id == rhs.Id && lhs.Status == rhs.Status && lhs.Error == rhs.Error && lhs.ExitCode == rhs.ExitCode &&
        lhs.ProcessResources == rhs.ProcessResources && EqualTime(lhs.StartedAt, rhs.StartedAt) &&
        EqualTime(lhs.FinishedAt, rhs.FinishedAt) && lhs.Stdout == rhs.Stdout && lhs.Stderr == rhs.Stderr &&
        (lhs.Dependencies is { } edges ? rhs.Dependencies is { } other && edges.SequenceEqual(other) : rhs.Dependencies is null);

    private static bool EqualTime(DateTimeOffset? lhs, DateTimeOffset? rhs) =>
        lhs is { } left ? rhs is { } right && left.EqualsExact(right) : rhs is null;

    private static void WriteNode(Wire.ProcessNode.Builder wire, ProcessNodeReport node)
    {
        wire.SetId(node.Id);
        wire.SetStatus(node.Status);
        wire.SetError(node.Error);
        wire.SetStdout(node.Stdout);
        wire.SetStderr(node.Stderr);
        if (node.ExitCode is { } code)
        {
            wire.InitExitCode().SetValue(code);
        }
        if (node.StartedAt is { } started)
        {
            WriteTime(wire.InitStartedAt(), started);
        }
        if (node.FinishedAt is { } finished)
        {
            WriteTime(wire.InitFinishedAt(), finished);
        }
        if (node.Dependencies is { } deps)
        {
            Wire.ProcessNode.DependenciesListBuilder list = wire.InitDependencies(deps.Count);
            for (int i = 0; i < list.Count; i++)
            {
                list.Set(i, deps[i]);
            }
        }
        if (node.ProcessResources is { } resources)
        {
            Report.ProcessResourceUsage.Builder target = wire.InitResources();
            target.SetSchemaVersion(resources.SchemaVersion);
            target.SetScope(resources.Scope);
            target.SetMethod(resources.Method);
            target.SetSampleIntervalMilliseconds(resources.SampleIntervalMilliseconds);
            target.SetSamples(resources.Samples);
            if (resources.PeakRssBytes is { } rss)
            {
                target.InitPeakRssBytes().SetValue(rss);
            }
            if (resources.CpuSeconds is { } cpu)
            {
                target.InitCpuSeconds().SetValue(cpu);
            }
        }
    }

    private static ProcessNodeReport ReadNode(Wire.ProcessNode.Reader wire)
    {
        List<string>? dependencies = null;
        if (wire.HasDependencies)
        {
            dependencies = new(wire.Dependencies.Count);
            for (int i = 0; i < wire.Dependencies.Count; i++)
            {
                dependencies.Add(Required(wire.Dependencies.Decode(i)));
            }
        }
        return new()
        {
            Id = Required(wire.DecodeId()),
            Status = Required(wire.DecodeStatus()),
            Error = wire.DecodeError(),
            Stdout = wire.DecodeStdout(),
            Stderr = wire.DecodeStderr(),
            Dependencies = dependencies,
            ExitCode = wire.HasExitCode ? wire.ExitCode.Value : null,
            StartedAt = wire.HasStartedAt ? Time(wire.StartedAt) : null,
            FinishedAt = wire.HasFinishedAt ? Time(wire.FinishedAt) : null,
            ProcessResources = wire.HasResources ? ReadResources(wire.Resources) : null
        };
    }

    private static ProcessResourceUsage ReadResources(Report.ProcessResourceUsage.Reader wire) =>
        new(wire.SchemaVersion, Required(wire.DecodeScope()), Required(wire.DecodeMethod()),
            wire.SampleIntervalMilliseconds, wire.Samples, wire.HasPeakRssBytes ? wire.PeakRssBytes.Value : null,
            wire.HasCpuSeconds ? wire.CpuSeconds.Value : null);

    private static void WriteTime(Report.Timestamp.Builder wire, DateTimeOffset value)
    {
        wire.SetTicks(value.Ticks);
        wire.SetOffsetMinutes((short)value.Offset.TotalMinutes);
    }

    private static DateTimeOffset Time(Report.Timestamp.Reader wire) => new(wire.Ticks, TimeSpan.FromMinutes(wire.OffsetMinutes));
    private static string Required(string? value) => value ?? throw new InvalidDataException("Missing native process field.");
}
