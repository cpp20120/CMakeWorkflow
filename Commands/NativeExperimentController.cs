using System.Security.Cryptography;
using Capnp.Managed;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.Client.Experiments;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Storage.Documents;
using Wire = Capnp.Generated.ExperimentController;

namespace CMakeWorkflow.Commands;

internal static class NativeExperimentController
{
    internal const string PlanFile = "plan.capnp";

    internal static ExperimentPlanner.ControllerPlanning Planning { get; } =
        new(PlanFile, EncodeAsync, ReadAsync);

    private static string WorkflowStore(string root) => Path.Combine(root, ".controller-workflows");

    private static async Task<(string Id, byte[] Frame)> EncodeAsync(ExperimentPlan plan, string root, CancellationToken ct)
    {
        ReadOnlyMemory<byte> workflow = plan.BinaryManifest ??
            throw new InvalidDataException("Native controller planning requires a binary workflow.");
        DocumentStore store = await DocumentStore.OpenProductionStoreAsync(WorkflowStore(root), ct);
        DocumentDescriptor descriptor = await store.ImportBinaryRetainedAsync(workflow.Span,
            "controller-workflow/" + plan.ManifestSha256.ToUpperInvariant(), ct);
        if (!descriptor.Blob.Value.Equals(plan.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The retained controller workflow differs from the plan digest.");
        }

        byte[] frame = Encode(plan, descriptor);
        return (Approval(frame), frame);
    }

    internal static async Task<ExperimentPlan> ReadAsync(string path, string id, CancellationToken ct)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length is < 16 or > 1024 * 1024)
        {
            throw new InvalidDataException("Invalid native controller plan size.");
        }

        byte[] frame = new byte[stream.Length];
        await stream.ReadExactlyAsync(frame, ct);
        if (Approval(frame) != id)
        {
            throw new InvalidDataException("Native controller plan does not match its approval identity.");
        }

        (ExperimentPlan plan, DocumentDescriptor workflow) = Decode(frame);
        string root = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        DocumentStore store = DocumentStore.OpenExistingProductionStore(WorkflowStore(root));
        using DocumentSession session = await store.OpenAsync(workflow.Id, ct: ct);
        return session.Descriptor == workflow
            ? plan with { BinaryManifest = session.ExportStoredBytes() }
            : throw new InvalidDataException("Native controller workflow differs from its retained descriptor.");
    }

    private static string Approval(ReadOnlySpan<byte> frame)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("cmwf-experiment-controller-approval-v2\n"u8);
        hash.AppendData(frame);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] Encode(ExperimentPlan plan, DocumentDescriptor workflow)
    {
        ExperimentSourceBundle bundle = plan.Bundle ?? throw new InvalidDataException("Missing experiment source bundle.");
        MessageBuilder builder = new(MessageLimits.Default);
        Wire.ControllerPlan.Builder root = Wire.ControllerPlan.InitRoot(builder);
        root.SetVersion(4);
        root.SetRepository(plan.Repository);
        root.SetRequestedRef(plan.RequestedRef);
        root.SetCommit(plan.Commit);
        root.SetSourceSnapshotId(plan.SourceSnapshotId);
        root.SetBundleSha256(bundle.Sha256);
        root.SetBundleSize(checked((ulong)bundle.Size));
        root.SetBundleFileCount(checked((uint)bundle.FileCount));
        root.SetDependencyPlanSha256(plan.DependencyPlanSha256);
        Wire.Host.Builder host = root.InitHost();
        host.SetDestination(plan.Host.Destination);
        host.SetWorkspace(plan.Host.Workspace);
        host.SetEndpoint(plan.Host.Endpoint);
        host.SetExecutable(plan.Host.Executable);
        host.SetUsername(plan.Host.Username);
        host.SetKeyFile(plan.Host.KeyFile);
        host.SetHostKeySha256(plan.Host.HostKeySha256);
        host.SetPort(checked((ushort)plan.Host.Port));
        Wire.Workflow.Builder reference = root.InitWorkflow();
        reference.SetDocumentId(workflow.Id.Value);
        reference.SetBlobId(workflow.Blob.Value);
        reference.SetCanonicalDigest(workflow.Canonical!.Value.Value);
        reference.SetSchemaVersion(checked((ushort)workflow.SchemaVersion));
        reference.SetStoredLength(checked((uint)workflow.StoredLength));
        if (plan.Placement is { } placement)
        {
            Wire.Placement.Builder target = root.InitPlacement();
            target.SetName(placement.Name);
            target.SetMinimumProcessors(checked((uint)placement.MinProcessors));
            target.SetArchitecture(placement.Resources.Architecture);
            target.SetAvailableProcessors(checked((uint)placement.Resources.AvailableProcessors));
            target.SetCompilerVersion(placement.Resources.ManagedCompilerVersion);
            target.SetToolchainFingerprint(placement.Resources.ManagedToolchainFingerprint);
        }

        return CapnpWire.Frame(CapnpWire.Canonicalize(builder.CopySegment()));
    }

    private static (ExperimentPlan, DocumentDescriptor) Decode(ReadOnlySpan<byte> frame)
    {
        CapnpWire.Validate(CapnpWire.Unframe(frame));
        Wire.ControllerPlan.Reader root = Wire.ControllerPlan.ReadRoot(FrameCodec.Read(frame));
        if (root.Version != 4 || !root.HasHost || !root.HasWorkflow ||
            root.BundleSize is < 1 or > ExperimentSourceBundle.MaximumArchiveBytes ||
            root.BundleFileCount is < 1 or > ExperimentSourceBundle.MaximumFiles)
        {
            throw new InvalidDataException("Unsupported native controller plan.");
        }

        Wire.Host.Reader remote = root.Host;
        ExperimentHost host = new ExperimentHost(Required(remote.DecodeDestination()), Required(remote.DecodeWorkspace()),
            Required(remote.DecodeEndpoint()), Required(remote.DecodeExecutable()), remote.DecodeUsername(),
            remote.DecodeKeyFile(), remote.DecodeHostKeySha256(), remote.Port).Normalize();
        Wire.Workflow.Reader workflow = root.Workflow;
        DocumentDescriptor descriptor = DocumentDescriptor.Create(DocumentCodec.ProductionWorkflowCapnp, workflow.SchemaVersion,
            new(Required(workflow.DecodeBlobId())), new CanonicalDigest(Required(workflow.DecodeCanonicalDigest())), workflow.StoredLength);
        if (descriptor.Id.Value != workflow.DecodeDocumentId())
        {
            throw new InvalidDataException("Invalid workflow descriptor in native controller plan.");
        }

        ExperimentPlacement? placement = null;
        if (root.HasPlacement)
        {
            Wire.Placement.Reader selected = root.Placement;
            placement = new(Required(selected.DecodeName()), checked((int)selected.MinimumProcessors),
                new(Required(selected.DecodeArchitecture()), checked((int)selected.AvailableProcessors),
                    selected.DecodeCompilerVersion(), selected.DecodeToolchainFingerprint()));
        }

        return (new(4, Required(root.DecodeRepository()), Required(root.DecodeRequestedRef()), Required(root.DecodeCommit()),
            host, Digest(root.DecodeSourceSnapshotId(), upper: true), descriptor.Blob.Value.ToUpperInvariant(), "", [])
        {
            Bundle = new(Digest(root.DecodeBundleSha256(), upper: true), (long)root.BundleSize, (int)root.BundleFileCount),
            Placement = placement,
            DependencyPlanSha256 = root.HasDependencyPlanSha256 ? Digest(root.DecodeDependencyPlanSha256()) : null
        }, descriptor);
    }

    private static string Digest(string? value, bool upper = false) => value is { Length: 64 } &&
        (upper ? value.All(char.IsAsciiHexDigitUpper) : value.All(char.IsAsciiHexDigitLower))
        ? value : throw new InvalidDataException("Invalid native controller digest.");

    private static string Required(string? value) => !string.IsNullOrWhiteSpace(value)
        ? value : throw new InvalidDataException("Missing native controller field.");
}
