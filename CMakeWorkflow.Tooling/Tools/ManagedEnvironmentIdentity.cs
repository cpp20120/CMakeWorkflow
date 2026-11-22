using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

#if EXPERIMENT_SDK
namespace CMakeWorkflow.Client.Experiments;
#else
namespace CMakeWorkflow.Tooling.Tools;
#endif

internal sealed record ManagedEnvironmentLock(int SchemaVersion, string Profile, string TargetTriple,
    string Runtime, IReadOnlyList<ManagedToolRelease> Releases);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ManagedEnvironmentLock))]
[JsonSerializable(typeof(VcpkgProjectPlan))]
internal sealed partial class ManagedPlanJson : JsonSerializerContext;

internal static class ManagedEnvironmentIdentity
{
    internal const string TargetTriple = "x86_64-unknown-linux-gnu";
    internal const string Runtime = "glibc >= 2.31, libstdc++ (GCC 10 ABI), libgcc_s";

    internal static byte[] SerializeEnvironment() => JsonSerializer.SerializeToUtf8Bytes(
        new ManagedEnvironmentLock(1, "linux-managed-clang", ManagedEnvironmentIdentity.TargetTriple, ManagedEnvironmentIdentity.Runtime,
            [ManagedReleases.CMakeRelease, ManagedReleases.NinjaRelease, ManagedReleases.LlvmRelease, ManagedReleases.SysrootRelease]),
        ManagedPlanJson.Default.ManagedEnvironmentLock);

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
