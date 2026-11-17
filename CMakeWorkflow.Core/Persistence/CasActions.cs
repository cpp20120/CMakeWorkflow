using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.Core.Persistence;

internal sealed record CasActionResult(int SchemaVersion, string ActionDigest, string Digest, long Size);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CasActionResult))]
internal sealed partial class CasActionJson : JsonSerializerContext;

internal sealed class CasActions(ArtifactStoreFiles files)
{
    internal async Task<bool> TryRestoreAsync(string key, string output, CancellationToken ct)
    {
        await using FileStream lease = await files.OpenLeaseAsync(true, ct);
        if (!File.Exists(PathFor(files, key)))
        {
            return false;
        }

        CasActionResult entry = await ReadAsync(files, key, ct);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await files.RestoreAsync(entry.Digest, entry.Size, output, ct);
        return true;
    }

    internal async Task PublishAsync(string key, string output, string expectedDigest, CancellationToken ct)
    {
        FileInfo source = new(output);
        if (!source.Exists || source.LinkTarget is not null)
        {
            throw new InvalidDataException("A cached action must produce a regular file.");
        }

        if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            && (File.GetUnixFileMode(output)
                & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
        {
            throw new InvalidDataException("The action cache supports data files.");
        }

        CasActionResult result = new(1, key, ArtifactStoreFiles.NormalizeDigest(expectedDigest), source.Length);
        await using FileStream lease = await files.OpenLeaseAsync(true, ct);
        string reference = PathFor(files, key);
        if (File.Exists(reference) && await ReadAsync(files, key, ct) != result)
        {
            throw new InvalidDataException("Identical cached action inputs produced different output evidence.");
        }

        string blob = files.Blob(result.Digest);
        if (File.Exists(blob))
        {
            await ArtifactStoreFiles.VerifyAsync(blob, result.Digest, result.Size, ct);
        }
        else
        {
            string stage = Path.Combine(files.Staging, Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(stage);
            string temporary = Path.Combine(stage, "blob");
            try
            {
                await ArtifactStoreFiles.CopyAsync(output, temporary, ct);
                await ArtifactStoreFiles.VerifyAsync(temporary, result.Digest, result.Size, ct);
                ct.ThrowIfCancellationRequested();
                DurableByteWriter.PublishNewFile(temporary, blob);
            }
            finally
            {
                Directory.Delete(stage, recursive: true);
            }
        }

        if (!File.Exists(reference))
        {
            await DurableByteWriter.WriteAsync(reference,
                JsonSerializer.SerializeToUtf8Bytes(result, CasActionJson.Default.CasActionResult), ct);
        }
    }

    internal async Task DropAsync(string key, CancellationToken ct)
    {
        await using FileStream lease = await files.OpenLeaseAsync(false, ct);
        File.Delete(PathFor(files, ArtifactStoreFiles.NormalizeDigest(key)));
    }

    // Callers own the store lease while reading references for collection.
    internal static async Task<CasActionResult> ReadAsync(ArtifactStoreFiles files, string key, CancellationToken ct)
    {
        string path = PathFor(files, key);
        if (new FileInfo(path).Length > 4096)
        {
            throw new InvalidDataException("CAS action reference exceeds 4 KiB.");
        }

        CasActionResult? result = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path, ct),
            CasActionJson.Default.CasActionResult);
        return result is not { SchemaVersion: 1, Size: >= 0 } || result.ActionDigest != key
               || !ArtifactStoreFiles.IsCanonicalHex(result.Digest, 64)
            ? throw new InvalidDataException("Invalid CAS action reference.")
            : result;
    }

    private static string PathFor(ArtifactStoreFiles files, string key) =>
        ArtifactStoreFiles.IsCanonicalHex(key, 64)
            ? Path.Combine(files.Actions, key + ".json")
            : throw new ArgumentException("Invalid action digest.", nameof(key));
}
