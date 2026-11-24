using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling.Tools;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(VcpkgProjectReceipt))]
internal sealed partial class ManagedEnvironmentJson : JsonSerializerContext;

public static class ManagedEnvironmentLocks
{
    internal static byte[] Serialize() => ManagedEnvironmentIdentity.SerializeEnvironment();
    internal static string Hash(ReadOnlySpan<byte> bytes) => ManagedEnvironmentIdentity.Hash(bytes);

    internal static void RequireDigest(string digest)
    {
        if (digest is not { Length: 64 } || digest.Any(c => !char.IsAsciiHexDigitLower(c)))
        {
            throw new ArgumentException("Expected a lowercase SHA-256 digest.", nameof(digest));
        }
    }

    public static async Task VerifyAsync(string digest, CancellationToken ct)
        => _ = await ReadAsync(digest, ct);

    internal static async Task<ToolchainEnvironmentLock?> ReadAsync(string digest, CancellationToken ct)
    {
        RequireDigest(digest);
        string path = ManifestPath(digest);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Environment lock {digest} is missing. Install it using tooling lock --write.");
        }

        if (new FileInfo(path).Length > 128 * 1024)
        {
            throw new InvalidDataException("The toolchain environment lock exceeds its size limit.");
        }
        byte[] expected = Serialize();
        byte[] actual = await File.ReadAllBytesAsync(path, ct);
        return Hash(actual) != digest
            ? throw new InvalidDataException("The environment lock differs from this CLI's pinned Linux toolchain or its approved SHA-256.")
            : Hash(expected) == digest ? null : ToolchainEnvironment.Validate(actual);
    }

    internal static async Task WriteAsync(byte[] bytes, string digest, CancellationToken ct)
    {
        RequireDigest(digest);
        if (Hash(bytes) != digest)
        {
            throw new InvalidDataException("Cannot publish an environment lock under a different digest.");
        }
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
