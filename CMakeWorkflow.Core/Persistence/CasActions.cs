using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.Core.Persistence;

internal sealed record CasActionResult(int SchemaVersion, string ActionDigest, string Digest, long Size);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CasActionResult))]
internal sealed partial class CasActionJson : JsonSerializerContext;

internal sealed class CasActions(ArtifactStoreFiles files, int keyPolicy = 1)
{
    internal async Task<bool> TryRestoreAsync(string key, string output, CancellationToken ct)
    {
        await using FileStream lease = await files.OpenLeaseAsync(true, ct);
        string reference = PathFor(files, key);
        using CasReferenceIndex? index = await files.HasIndexedActionsAsync(ct) ? new(files) : null;
        CasActionResult? entry = index is not null ? index.Find(key, keyPolicy)
            : File.Exists(reference) ? await ReadAsync(files, key, ct) : null;
        if (entry is null)
        {
            return false;
        }

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
        using CasReferenceIndex? index = await files.HasIndexedActionsAsync(ct) ? new(files) : null;
        CasActionResult? existing = index is not null ? index.Find(key, keyPolicy)
            : File.Exists(reference) ? await ReadAsync(files, key, ct) : null;
        if (existing is not null && existing != result)
        {
            throw new InvalidDataException("Identical cached action inputs produced different output evidence.");
        }

        await PublishBlobAsync(output, result, ct);

        if (existing is null && index is not null)
        {
            ct.ThrowIfCancellationRequested();
            index.Publish(result, keyPolicy);
        }
        else if (existing is null)
        {
            await DurableByteWriter.WriteAsync(reference,
                JsonSerializer.SerializeToUtf8Bytes(result, CasActionJson.Default.CasActionResult), ct);
        }
    }

    // Acquire the legacy source first. An indexed source is rejected before acquiring
    // any destination lock, including when both paths refer to the same store.
    internal async Task<int> ImportLegacyAsync(ArtifactStoreFiles source, Func<Task> beforeCommit, CancellationToken ct)
    {
        if (keyPolicy is not (1 or 2))
        {
            throw new InvalidOperationException("Action Cache import requires a supported key policy.");
        }

        await using FileStream sourceLease = await source.OpenLeaseAsync(create: false, ct);
        if (await source.HasIndexedActionsAsync(ct))
        {
            throw new InvalidDataException("Action Cache import requires a legacy JSON source store.");
        }

        await using FileStream destinationLease = await files.OpenLeaseAsync(create: false, ct, requireIndexedActions: true);
        using CasReferenceIndex index = new(files);
        List<CasActionResult> additions = [];
        foreach (string path in Directory.EnumerateFiles(source.Actions, "*.json").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string key = Path.GetFileNameWithoutExtension(path);
            CasActionResult result = await ReadAsync(source, key, ct);
            CasActionResult? existing = index.Find(key, keyPolicy);
            if (existing is not null && existing != result)
            {
                throw new InvalidDataException("Identical cached action inputs produced different output evidence.");
            }

            string blob = source.Blob(result.Digest);
            if (new FileInfo(blob).LinkTarget is not null)
            {
                throw new InvalidDataException("A legacy action blob cannot be redirected.");
            }

            await ArtifactStoreFiles.VerifyAsync(blob, result.Digest, result.Size, ct);
            await PublishBlobAsync(blob, result, ct);
            if (existing is null)
            {
                additions.Add(result);
            }
        }

        if (additions.Count > 0)
        {
            await beforeCommit();
            index.Publish(additions, keyPolicy, ct);
        }

        return additions.Count;
    }

    private async Task PublishBlobAsync(string output, CasActionResult result, CancellationToken ct)
    {
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

    }

    internal async Task DropAsync(string key, CancellationToken ct)
    {
        await using FileStream lease = await files.OpenLeaseAsync(false, ct);
        string normalized = ArtifactStoreFiles.NormalizeDigest(key);
        if (await files.HasIndexedActionsAsync(ct))
        {
            using CasReferenceIndex index = new(files);
            index.Drop(normalized);
        }
        else
        {
            File.Delete(PathFor(files, normalized));
        }
    }

    // Callers own the store lease while reading references for collection.
    internal static async Task AddRootsAsync(ArtifactStoreFiles files, ISet<string> reachable, CancellationToken ct)
    {
        IEnumerable<string> legacy = Directory.EnumerateFiles(files.Actions, "*.json");
        if (await files.HasIndexedActionsAsync(ct))
        {
            if (legacy.Any())
            {
                throw new InvalidDataException("Legacy action reference found in an indexed CAS. Restore the original store format.");
            }

            using CasReferenceIndex index = new(files);
            index.AddRoots(reachable, ct);
            return;
        }

        foreach (string path in legacy)
        {
            CasActionResult entry = await ReadAsync(files, Path.GetFileNameWithoutExtension(path), ct);
            _ = reachable.Add(entry.Digest);
        }
    }

    private static async Task<CasActionResult> ReadAsync(ArtifactStoreFiles files, string key, CancellationToken ct)
    {
        string path = PathFor(files, key);
        FileInfo reference = new(path);
        if (reference.LinkTarget is not null || reference.Length > 4096)
        {
            throw new InvalidDataException("CAS action reference must be a regular file of at most 4 KiB.");
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
