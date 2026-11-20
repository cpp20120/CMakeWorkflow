using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling.Tools;

internal sealed record ManagedEnvironmentLock(int SchemaVersion, string Profile, string TargetTriple,
    string Runtime, IReadOnlyList<ManagedToolRelease> Releases);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ManagedEnvironmentLock))]
[JsonSerializable(typeof(VcpkgProjectPlan))]
[JsonSerializable(typeof(VcpkgProjectReceipt))]
internal sealed partial class ManagedEnvironmentJson : JsonSerializerContext;

public static class ManagedEnvironmentLocks
{
    internal static byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(
        new ManagedEnvironmentLock(1, "linux-managed-clang", ManagedLinuxSdk.TargetTriple, ManagedLinuxSdk.Runtime,
            [ToolCatalog.CMakeRelease, ToolCatalog.NinjaRelease, ToolCatalog.LlvmRelease, ToolCatalog.SysrootRelease]),
        ManagedEnvironmentJson.Default.ManagedEnvironmentLock);

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static void RequireDigest(string digest)
    {
        if (digest is not { Length: 64 } || digest.Any(c => !char.IsAsciiHexDigitLower(c)))
        {
            throw new ArgumentException("Expected a lowercase SHA-256 digest.", nameof(digest));
        }
    }

    public static async Task VerifyAsync(string digest, CancellationToken ct)
    {
        RequireDigest(digest);
        string path = ManifestPath(digest);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Environment lock {digest} is missing. Install it using tooling lock --write.");
        }

        byte[] expected = Serialize();
        if (Hash(expected) != digest || new FileInfo(path).Length != expected.Length ||
            !(await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(expected))
        {
            throw new InvalidDataException("The environment lock differs from this CLI's pinned Linux toolchain.");
        }
    }

    internal static async Task WriteAsync(byte[] bytes, string digest, CancellationToken ct)
    {
        string path = ManifestPath(digest);
        ManagedToolStore.EnsureDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                await VerifyAsync(digest, ct);
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string ManifestPath(string digest)
    {
        string directory = Path.Combine(ManagedToolStore.GetRoot(), "manifests");
        ManagedToolStore.CheckDirectoryPath(directory);
        string path = Path.Combine(directory, digest + ".json");
        ManagedToolStore.RejectLink(new FileInfo(path));
        return path;
    }
}
