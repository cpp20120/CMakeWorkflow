using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.Contracts.Kits;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Build;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(EngineeringKitLock))]
[JsonSerializable(typeof(EngineeringKitDefinition))]
internal sealed partial class KitEvidenceJson : JsonSerializerContext;

internal static class EngineeringKitEvidence
{
    internal const string Store = "cmwf-kits";
    internal static string PathFor(string digest) => Store + "/" + digest + "/lock.json";

    internal static byte[] Serialize(EngineeringKitLock evidence) =>
        JsonSerializer.SerializeToUtf8Bytes(evidence, KitEvidenceJson.Default.EngineeringKitLock);

    internal static async Task<EngineeringKitLock> ReadAsync(string root, string digest, CancellationToken ct,
        Action<string>? readGuard = null)
    {
        if (!BuildVariantConfiguration.IsDigest(digest))
        {
            throw new InvalidDataException("invalid-kit-lock: expected lowercase SHA-256.");
        }

        byte[] bytes = await BuildVariantConfiguration.ReadBoundedAsync(
            BuildVariantConfiguration.CheckedPath(root, PathFor(digest), readGuard), ct);
        EngineeringKitLock? kit = JsonSerializer.Deserialize(bytes, KitEvidenceJson.Default.EngineeringKitLock);
        if (kit is null || BuildVariantConfiguration.Hash(bytes) != digest || !bytes.AsSpan().SequenceEqual(Serialize(kit)))
        {
            throw new InvalidDataException("kit-lock-changed: expected canonical Kit evidence matching its SHA-256.");
        }
        Validate(kit);
        return kit;
    }

    internal static void RequireSupportedDependencies(string root, Action<string>? readGuard = null)
    {
        string manifest = BuildVariantConfiguration.CheckedPath(root, "vcpkg.json");
        if (File.Exists(manifest))
        {
            readGuard?.Invoke(manifest);
            throw new InvalidDataException("unsupported-kit-dependencies: use an explicit workflow with a verified vcpkg receipt.");
        }
    }

    internal static void Validate(EngineeringKitLock kit)
    {
        if (kit is not
            {
                SchemaVersion: 1, Backend: "cmake",
                ExecutionHost: { Os: "linux", Architecture: "x64", Abi: null },
                Target: { Os: "linux", Architecture: "x64", Abi: "gnu" },
                CMake.Generator: "Ninja" or "Ninja Multi-Config"
            } || !Enum.IsDefined(kit.Provider))
        {
            throw new InvalidDataException("unsupported-kit: requires a native Linux x64 GNU target and Ninja.");
        }

        bool managed = kit.Provider == KitProvider.ManagedClang;
        if (managed ? !BuildVariantConfiguration.IsDigest(kit.EnvironmentLockSha256) : kit.EnvironmentLockSha256 is not null)
        {
            throw new InvalidDataException("invalid-kit-environment: managed Kits require an exact environment lock.");
        }
    }

    internal static VariantEnvironment Compose(EngineeringKitLock kit, VariantEnvironment sdkEnvironment, SdkDescriptor sdk)
    {
        Validate(kit);
        string capability = kit.Provider == KitProvider.ManagedClang ? "linux-managed-clang" : "ambient";
        bool compatible = sdk.Capabilities.ExecutionModes.Contains(capability, StringComparer.Ordinal) &&
            (sdkEnvironment.EnvironmentLockSha256 is not { } pin ||
                (pin == kit.EnvironmentLockSha256 && sdkEnvironment.Generator == kit.CMake.Generator));
        return compatible ? new(kit.ExecutionHost.Os, kit.ExecutionHost.Architecture, kit.Target.Os,
            kit.Target.Architecture, kit.Target.Abi!, kit.CMake.Generator, kit.EnvironmentLockSha256) :
            throw new InvalidDataException("incompatible-sdk-kit: the selected Kit does not satisfy SDK constraints.");
    }

    internal static WorkflowToolchainBinding Toolchain(EngineeringKitLock kit) => new(
        EnvironmentProfileId: kit.Provider switch
        {
            KitProvider.ManagedClang => "linux-managed-clang",
            KitProvider.AmbientGcc => "linux-ambient-gcc",
            KitProvider.AmbientClang => "linux-ambient-clang",
            _ => throw new InvalidDataException("unsupported-kit-provider")
        },
        Generator: kit.CMake.Generator, HostArchitecture: kit.ExecutionHost.Architecture, TargetArchitecture: kit.Target.Architecture)
    {
        EnvironmentLockSha256 = kit.EnvironmentLockSha256
    };
}
