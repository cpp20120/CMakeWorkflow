using Capnp.Managed;
using CMakeWorkflow.Binary.Capnp;
using Wire = Capnp.Generated.ManagedToolchain;

namespace CMakeWorkflow.Tooling.Tools;

internal static class BinaryEnvironmentLock
{
    internal static byte[] Encode(ToolchainEnvironmentLock environment)
    {
        MessageBuilder message = new(ManagedToolchainWire.Limits);
        Wire.EnvironmentLock.Builder root = Wire.EnvironmentLock.InitRoot(message);
        root.SetFormatVersion(1);
        root.SetSchemaVersion((uint)environment.SchemaVersion);
        root.SetProfile(environment.Profile);
        root.SetHostOs(environment.Host.Os);
        root.SetHostArchitecture(environment.Host.Architecture);
        root.SetTargetOs(environment.Target.Os);
        root.SetTargetArchitecture(environment.Target.Architecture);
        root.SetTargetAbi(environment.Target.Abi);
        root.SetTargetTriple(environment.Target.Triple);
        root.SetGenerator(environment.Generator);
        root.SetPolicy(environment.Policy.ToString());
        Wire.EnvironmentLock.ComponentsListBuilder components = root.InitComponents(environment.Components.Length);
        for (int i = 0; i < components.Count; i++)
        {
            EnvironmentComponent component = environment.Components[i];
            Wire.Component.Builder value = components[i];
            value.SetId(component.Id);
            value.SetVersion(component.Version);
            value.SetOrigin(component.Origin);
            ManagedToolchainWire.WriteRelease(value.InitDistribution(), component.Distribution);
            Wire.Component.DependenciesListBuilder dependencies = value.InitDependencies(component.Dependencies.Length);
            for (int j = 0; j < dependencies.Count; j++)
            {
                dependencies[j].SetId(component.Dependencies[j].Id);
                dependencies[j].SetVersion(component.Dependencies[j].Version);
            }
        }

        return CapnpWire.Frame(CapnpWire.Canonicalize(message.CopySegment()));
    }

    internal static string Identity(ReadOnlySpan<byte> frame) =>
        ManagedToolchainWire.Identity("cmwf-managed-toolchain-environment-v1\n"u8, frame);

    internal static ToolchainEnvironmentLock Decode(ReadOnlySpan<byte> frame)
    {
        Wire.EnvironmentLock.Reader root = Wire.EnvironmentLock.ReadRoot(FrameCodec.Read(frame, ManagedToolchainWire.Limits));
        if (root.FormatVersion != 1 || root.SchemaVersion != 2 ||
            root.DecodePolicy() != nameof(ToolResolutionPolicy.ManagedOnly) || root.Components.Count != 4)
        {
            throw new InvalidDataException("Unsupported binary managed environment lock.");
        }

        List<ComponentRequirement> versions = [];
        foreach (Wire.Component.Reader component in root.Components)
        {
            versions.Add(new(component.DecodeId() ?? "", component.DecodeVersion() ?? ""));
        }
        (ToolchainEnvironmentLock? expected, ToolchainDiagnostic? error) = ToolchainEnvironment.Resolve(new(
            root.DecodeProfile() ?? "", new(root.DecodeHostOs() ?? "", root.DecodeHostArchitecture() ?? ""),
            new(root.DecodeTargetOs() ?? "", root.DecodeTargetArchitecture() ?? "", root.DecodeTargetAbi() ?? "",
                root.DecodeTargetTriple() ?? ""), root.DecodeGenerator() ?? "", ToolResolutionPolicy.ManagedOnly,
            [.. versions]), ManagedComponentCatalog.Components);

        // Equality to the resolved canonical frame verifies every distribution and dependency,
        // including fields added in a later wire contract that this reader cannot consume.
        return expected is not null && frame.SequenceEqual(Encode(expected))
            ? expected
            : throw new InvalidDataException(error?.Message ?? "Binary environment differs from the pinned catalog.");
    }
}
