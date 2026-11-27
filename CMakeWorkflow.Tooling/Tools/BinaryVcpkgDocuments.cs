using Capnp.Managed;
using CMakeWorkflow.Binary.Capnp;
using Wire = Capnp.Generated.ManagedToolchain;

namespace CMakeWorkflow.Tooling.Tools;

internal static class BinaryVcpkgDocuments
{
    internal static byte[] EncodeReceipt(VcpkgProjectReceipt receipt)
    {
        MessageBuilder message = new(ManagedToolchainWire.Limits);
        Wire.ProvisioningReceipt.Builder root = Wire.ProvisioningReceipt.InitRoot(message);
        root.SetFormatVersion(1);
        BinaryVcpkgPlan.WritePlan(root.InitPlan(), receipt.Plan);
        root.SetPlanIdentity(BinaryVcpkgPlan.Identity(BinaryVcpkgPlan.Encode(receipt.Plan)));
        root.SetInstalledTreeSha256(receipt.InstalledTreeSha256);
        root.SetToolchainSha256(receipt.ToolchainSha256);
        root.SetDownloadTreeSha256(receipt.DownloadTreeSha256);
        return CapnpWire.Frame(CapnpWire.Canonicalize(message.CopySegment()));
    }

    internal static VcpkgProjectReceipt DecodeReceipt(ReadOnlySpan<byte> frame)
    {
        Wire.ProvisioningReceipt.Reader root = Wire.ProvisioningReceipt.ReadRoot(FrameCodec.Read(frame, ManagedToolchainWire.Limits));
        Wire.DependencyPlan.Reader document = root.Plan;
        if (root.FormatVersion != 1 || document.FormatVersion != 1 || document.SchemaVersion is not (1 or 2))
        {
            throw new InvalidDataException("Unsupported native vcpkg receipt or dependency plan.");
        }

        VcpkgProjectPlan plan = new((int)document.SchemaVersion, document.DecodeDirectory() ?? "",
            document.DecodeManifestSha256() ?? "", document.DecodeConfigurationSha256(),
            document.DecodeEnvironmentIdentity() ?? "", ManagedToolchainWire.ReadRelease(document.Executable),
            ManagedToolchainWire.ReadRelease(document.Checkout))
        {
            Commit = document.DecodeCommit(),
            SourceSnapshotId = document.DecodeSourceSnapshotId(),
            TargetTriplet = document.DecodeTargetTriplet(),
            HostTriplet = document.DecodeHostTriplet(),
            Registries = document.DecodeRegistries()
        };
        VcpkgProjectReceipt receipt = new(plan, root.DecodeInstalledTreeSha256() ?? "", root.DecodeToolchainSha256() ?? "")
        {
            DownloadTreeSha256 = root.DecodeDownloadTreeSha256()
        };

        return root.DecodePlanIdentity() == BinaryVcpkgPlan.Identity(BinaryVcpkgPlan.Encode(plan)) && frame.SequenceEqual(EncodeReceipt(receipt))
            ? receipt : throw new InvalidDataException("The native receipt is noncanonical or binds a different dependency plan.");
    }

}
