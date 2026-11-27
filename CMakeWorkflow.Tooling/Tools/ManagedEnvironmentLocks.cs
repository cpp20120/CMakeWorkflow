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
        string binary = ManifestPath(digest, binary: true);
        if (File.Exists(binary))
        {
            return await ReadBinaryAsync(digest, ct);
        }

        string path = ManifestPath(digest, binary: false);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Environment lock {digest} is missing. Install it using tooling lock --write.");
        }

        byte[] expected = Serialize();
        byte[] actual = await ManagedToolchainWire.ReadBoundedAsync(path, ct);
        return Hash(actual) != digest
            ? throw new InvalidDataException("The environment lock differs from this CLI's pinned Linux toolchain or its approved SHA-256.")
            : Hash(expected) == digest ? null : ToolchainEnvironment.Validate(actual);
    }

    internal static async Task<ToolchainEnvironmentLock> ReadBinaryAsync(string digest, CancellationToken ct)
    {
        RequireDigest(digest);
        byte[] frame = await ManagedToolchainWire.ReadBoundedAsync(ManifestPath(digest, binary: true), ct);
        return BinaryEnvironmentLock.Identity(frame) == digest
            ? BinaryEnvironmentLock.Decode(frame)
            : throw new InvalidDataException("The binary environment lock differs from its approved identity.");
    }

    internal static Task WriteAsync(byte[] bytes, string digest, CancellationToken ct) =>
        WriteAsync(bytes, digest, binary: false, ct);

    internal static Task WriteBinaryAsync(byte[] frame, string digest, CancellationToken ct)
    {
        _ = BinaryEnvironmentLock.Decode(frame);
        return WriteAsync(frame, digest, binary: true, ct);
    }

    private static async Task WriteAsync(byte[] bytes, string digest, bool binary, CancellationToken ct)
    {
        RequireDigest(digest);
        if ((binary ? BinaryEnvironmentLock.Identity(bytes) : Hash(bytes)) != digest)
        {
            throw new InvalidDataException("Cannot publish an environment lock under a different digest.");
        }
        string path = ManifestPath(digest, binary);
        ManagedToolStore.EnsureDirectory(Path.GetDirectoryName(path)!);
        if (binary)
        {
            try
            {
                await ProvisioningInterruption.PauseAsync("before-environment-publication", ct);
                await ManagedToolchainWire.PublishAsync(path, bytes, ct);
            }
            catch (IOException) when (File.Exists(path))
            {
                _ = await ReadBinaryAsync(digest, ct);
            }
            return;
        }

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

    private static string ManifestPath(string digest, bool binary)
    {
        string directory = Path.Combine(ManagedToolStore.GetRoot(), "manifests");
        ManagedToolStore.CheckDirectoryPath(directory);
        string path = Path.Combine(directory, digest + (binary ? ".capnp" : ".json"));
        ManagedToolStore.RejectLink(new FileInfo(path));
        return path;
    }
}
