using Capnp.Managed;
using CMakeWorkflow.Binary.Capnp;
using Wire = Capnp.Generated.ManagedToolchain;

namespace CMakeWorkflow.Tooling.Tools;

internal static class BinaryVcpkgPlan
{
    internal static byte[] Encode(VcpkgProjectPlan plan)
    {
        MessageBuilder message = new(ManagedToolchainWire.Limits);
        WritePlan(Wire.DependencyPlan.InitRoot(message), plan);
        return CapnpWire.Frame(CapnpWire.Canonicalize(message.CopySegment()));
    }

    internal static string Identity(ReadOnlySpan<byte> frame) =>
        ManagedToolchainWire.Identity("cmwf-vcpkg-dependency-plan-v1\n"u8, frame);

    internal static void WritePlan(Wire.DependencyPlan.Builder root, VcpkgProjectPlan plan)
    {
        root.SetFormatVersion(1);
        root.SetSchemaVersion((uint)plan.SchemaVersion);
        root.SetDirectory(plan.Directory);
        root.SetManifestSha256(plan.ManifestSha256);
        root.SetConfigurationSha256(plan.ConfigurationSha256);
        root.SetEnvironmentIdentity(plan.EnvironmentSha256);
        ManagedToolchainWire.WriteRelease(root.InitExecutable(), plan.Executable);
        ManagedToolchainWire.WriteRelease(root.InitCheckout(), plan.Checkout);
        root.SetCommit(plan.Commit);
        root.SetSourceSnapshotId(plan.SourceSnapshotId);
        root.SetTargetTriplet(plan.TargetTriplet);
        root.SetHostTriplet(plan.HostTriplet);
        root.SetRegistries(plan.Registries);
    }
}
