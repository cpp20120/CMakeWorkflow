using System.Text.Json;
using System.Security.Cryptography;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Core.Persistence;
using CMakeWorkflow.Storage.Documents;

namespace CMakeWorkflow.Commands;

// The store is owned by one local trust domain. Every command takes the same
// lease, including blob copies, reference updates and the mark/sweep pass.
internal sealed class ArtifactCas(string directory)
{
    private readonly ArtifactStoreFiles files = new(directory);

    internal async Task<CasOperation> ImportAsync(WorkflowOperation operation, CancellationToken ct)
    {
        OperationSnapshot snapshot = await operation.GetSnapshotAsync(ct);
        OperationArtifactList catalog = await operation.ListArtifactsAsync(ct);
        string id = OperationId(snapshot.OperationId);
        string reportDigest = Digest(catalog.ReportSha256);
        if (OperationId(catalog.OperationId) != id || OperationId(operation.OperationId) != id
            || catalog.Artifacts.Count is < 1 or > 512
            || catalog.Artifacts.SingleOrDefault(a => a.ArtifactId == "report") is not { } report
            || report.Size is < 0 or > (16 * 1024 * 1024)
            || Digest(report.Sha256) != reportDigest)
        {
            throw new InvalidDataException("Artifact catalog does not match its finalized operation report.");
        }

        string? nativeDocument = catalog.NativeReportDocumentId is { } document ? Digest(document) : null;
        string? nativeBlob = catalog.NativeReportBlobId is { } blobId ? Digest(blobId) : null;
        if ((nativeDocument is null) != (nativeBlob is null) ||
            (nativeBlob is not null &&
             (catalog.Artifacts.SingleOrDefault(a => a.ArtifactId == "native-report") is not { } native ||
              Digest(native.Sha256) != nativeBlob)))
        {
            throw new InvalidDataException("Artifact catalog does not contain the referenced native report.");
        }

        CasArtifact[] entries = [.. catalog.Artifacts.Select(ToArtifact)
            .OrderBy(a => a.ArtifactId, StringComparer.Ordinal)];
        if (entries.Select(a => a.ArtifactId).Distinct(StringComparer.Ordinal).Count() != entries.Length)
        {
            throw new InvalidDataException("Duplicate artifact ID in finalized catalog.");
        }

        using FileStream lease = await files.OpenLeaseAsync(true, ct);
        using CasReferenceIndex? index = await files.HasIndexedActionsAsync(ct) ? new(files) : null;
        CasOperation? existing = await TryReadAsync(id, index, ct);
        if (existing is not null && (existing.WorkspaceId != snapshot.WorkspaceId
            || existing.ReportDigest != reportDigest || !existing.Artifacts.SequenceEqual(entries)
            || existing.NativeReportDocumentId != (index is null ? null : nativeDocument)
            || existing.NativeReportBlobId != (index is null ? null : nativeBlob)))
        {
            throw new InvalidDataException("This operation ID already names different artifact evidence.");
        }

        string stage = Path.Combine(files.Staging, Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(stage);
        try
        {
            foreach (OperationArtifactInfo artifact in catalog.Artifacts)
            {
                ct.ThrowIfCancellationRequested();
                string digest = Digest(artifact.Sha256);
                string blob = files.Blob(digest);
                if (File.Exists(blob))
                {
                    await ArtifactStoreFiles.VerifyAsync(blob, digest, artifact.Size, ct);
                    continue;
                }

                string downloaded = Path.Combine(stage, digest);
                _ = await operation.DownloadArtifactAsync(artifact, downloaded, ct: ct);
                await ArtifactStoreFiles.VerifyAsync(downloaded, digest, artifact.Size, ct);
                using (FileStream file = new(downloaded, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    RandomAccess.FlushToDisk(file.SafeFileHandle);
                }
                ct.ThrowIfCancellationRequested();
                DurableByteWriter.PublishNewFile(downloaded, blob);
            }

            string? hostId = existing?.HostId ?? await ReadHostIdAsync(files.Blob(reportDigest), ct);
            bool indexedNative = index is not null && nativeBlob is not null;
            CasOperation receipt = new(indexedNative ? 2 : 1, snapshot.WorkspaceId, id, hostId, reportDigest, true, entries)
            {
                NativeReportDocumentId = indexedNative ? nativeDocument : null,
                NativeReportBlobId = indexedNative ? nativeBlob : null
            };
            return existing is { Pinned: true } ? existing : await WriteAsync(receipt, index, ct);
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    internal async Task<CasOperation> ShowAsync(string operation, CancellationToken ct)
    {
        using FileStream lease = await files.OpenLeaseAsync(false, ct);
        using CasReferenceIndex? index = await files.HasIndexedActionsAsync(ct) ? new(files) : null;
        return await ReadAsync(OperationId(operation), index, ct);
    }

    internal async Task<CasOperation> PinAsync(string operation, bool pinned, CancellationToken ct)
    {
        using FileStream lease = await files.OpenLeaseAsync(false, ct);
        using CasReferenceIndex? index = await files.HasIndexedActionsAsync(ct) ? new(files) : null;
        CasOperation current = await ReadAsync(OperationId(operation), index, ct);
        if (pinned)
        {
            foreach (CasArtifact artifact in current.Artifacts.DistinctBy(a => a.Digest))
            {
                await ArtifactStoreFiles.VerifyAsync(files.Blob(artifact.Digest), artifact.Digest, artifact.Size, ct);
            }
        }

        CasOperation updated = current with { Pinned = pinned };
        return current.Pinned != pinned ? await WriteAsync(updated, index, ct) : updated;
    }

    internal async Task FetchAsync(string operation, string artifactId, string destination, CancellationToken ct)
    {
        using FileStream lease = await files.OpenLeaseAsync(false, ct);
        using CasReferenceIndex? index = await files.HasIndexedActionsAsync(ct) ? new(files) : null;
        CasOperation reference = await ReadAsync(OperationId(operation), index, ct);
        CasArtifact entry = reference.Artifacts.SingleOrDefault(a => a.ArtifactId == artifactId)
                          ?? throw new KeyNotFoundException("Unknown artifact for this operation.");
        await files.RestoreAsync(entry.Digest, entry.Size, destination, ct);
    }

    internal async Task<CasGcResult> CollectAsync(bool apply, CancellationToken ct)
    {
        using FileStream lease = await files.OpenLeaseAsync(false, ct);
        HashSet<string> reachable = new(StringComparer.Ordinal);
        IEnumerable<string> legacy = Directory.EnumerateFiles(files.Operations, "*.json");
        if (await files.HasIndexedActionsAsync(ct))
        {
            if (legacy.Any())
            {
                throw new InvalidDataException("Legacy operation receipt found in an indexed CAS.");
            }
            using CasReferenceIndex index = new(files);
            foreach (CasReferenceIndex.ReceiptReference reference in index.Receipts(ct))
            {
                CasOperation operation = await BinaryArtifactManifest.ReadAsync(files, reference, ct);
                _ = reachable.Add(reference.Blob);
                if (operation.Pinned)
                {
                    reachable.UnionWith(operation.Artifacts.Select(a => a.Digest));
                }
            }
        }
        else
        {
            foreach (string path in legacy)
            {
                ct.ThrowIfCancellationRequested();
                CasOperation operation = await ReadLegacyAsync(OperationId(Path.GetFileNameWithoutExtension(path)), ct);
                if (operation.Pinned)
                {
                    reachable.UnionWith(operation.Artifacts.Select(a => a.Digest));
                }
            }
        }

        await CasActions.AddRootsAsync(files, reachable, ct);

        long count, size, protectedBlobs;
        if (await files.HasDocumentFormatAsync(ct))
        {
            (CollectionResult result, size, protectedBlobs) =
                await DocumentStore.CollectArtifactNamespaceAsync(files.Root, reachable, apply, ct);
            count = result.BlobsRemoved;
        }
        else
        {
            (count, size) = CollectArtifacts(reachable, apply, ct);
            protectedBlobs = reachable.Count;
        }

        if (apply)
        {
            foreach (string stage in Directory.EnumerateDirectories(files.Staging))
            {
                ct.ThrowIfCancellationRequested();
                if (ArtifactStoreFiles.IsCanonicalHex(Path.GetFileName(stage), 32))
                {
                    Directory.Delete(stage, recursive: true);
                }
            }
        }

        return new(apply, protectedBlobs, count, size);
    }

    private (long Count, long Size) CollectArtifacts(HashSet<string> reachable, bool apply, CancellationToken ct)
    {
        long count = 0, size = 0;
        foreach (string path in Directory.EnumerateFiles(files.Blobs))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (!ArtifactStoreFiles.IsCanonicalHex(name, 64) || reachable.Contains(name))
            {
                continue;
            }

            count++;
            size += new FileInfo(path).Length;
            if (apply)
            {
                File.Delete(path);
            }
        }

        return (count, size);
    }

    private string Reference(string operation) => Path.Combine(files.Operations, operation + ".json");

    private async Task<CasOperation> ReadAsync(string operation, CasReferenceIndex? index, CancellationToken ct) =>
        await TryReadAsync(operation, index, ct) ?? throw new FileNotFoundException("Artifact manifest is missing.");

    private async Task<CasOperation?> TryReadAsync(string operation, CasReferenceIndex? index, CancellationToken ct)
    {
        if (index is null)
        {
            return File.Exists(Reference(operation)) ? await ReadLegacyAsync(operation, ct) : null;
        }

        CasReferenceIndex.ReceiptReference? reference = index.FindReceipt(operation);
        return reference.HasValue ? await BinaryArtifactManifest.ReadAsync(files, reference.Value, ct) : null;
    }

    private async Task<CasOperation> ReadLegacyAsync(string operation, CancellationToken ct)
    {
        string path = Reference(operation);
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("CAS operation reference exceeds 4 MiB.");
        }
        CasOperation? receipt = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path, ct),
            ArtifactCasJson.Default.CasOperation);
        return receipt is not { SchemaVersion: 1 } || receipt.OperationId != operation
            || receipt.Artifacts is not { Count: > 0 and <= 512 }
            || !ArtifactStoreFiles.IsCanonicalHex(receipt.ReportDigest, 64)
            || receipt.Artifacts.Any(a => a is null || !ArtifactStoreFiles.IsCanonicalHex(a.Digest, 64) || a.Size < 0)
            || receipt.Artifacts.SingleOrDefault(a => a.ArtifactId == "report")?.Digest != receipt.ReportDigest
            ? throw new InvalidDataException("Invalid CAS operation reference.")
            : receipt;
    }

    private async Task<CasOperation> WriteAsync(CasOperation receipt, CasReferenceIndex? index, CancellationToken ct)
    {
        if (index is null)
        {
            await DurableByteWriter.WriteAsync(Reference(receipt.OperationId),
                JsonSerializer.SerializeToUtf8Bytes(receipt, ArtifactCasJson.Default.CasOperation), ct);
            return receipt;
        }

        byte[] frame = BinaryArtifactManifest.Encode(receipt);
        string digest = Convert.ToHexStringLower(SHA256.HashData(frame));
        if (index.FindReceipt(receipt.OperationId) is { } prior)
        {
            if (prior.Blob != digest || prior.Size != frame.Length)
            {
                throw new InvalidDataException("Operation already has another immutable artifact manifest.");
            }
            ct.ThrowIfCancellationRequested();
            index.SetPinned(receipt.OperationId, receipt.Pinned);
        }
        else
        {
            string blob = files.Blob(digest);
            if (File.Exists(blob))
            {
                await ArtifactStoreFiles.VerifyAsync(blob, digest, frame.Length, ct);
            }
            else
            {
                string ephemeral = Path.Combine(files.Staging, Guid.NewGuid().ToString("N"));
                _ = Directory.CreateDirectory(ephemeral);
                try
                {
                    string stage = Path.Combine(ephemeral, "manifest");
                    await DurableByteWriter.WriteAsync(stage, frame, ct);
                    ct.ThrowIfCancellationRequested();
                    DurableByteWriter.PublishNewFile(stage, blob);
                }
                finally
                {
                    Directory.Delete(ephemeral, recursive: true);
                }
            }
            ct.ThrowIfCancellationRequested();
            index.PublishReceipt(new(receipt.OperationId, digest, frame.Length, receipt.Pinned));
        }

        return receipt with { ArtifactManifestBlobId = digest };
    }

    private static async Task<string?> ReadHostIdAsync(string path, CancellationToken ct)
    {
        await using FileStream file = File.OpenRead(path);
        using JsonDocument report = await JsonDocument.ParseAsync(file, cancellationToken: ct);
        return report.RootElement.TryGetProperty("environment", out JsonElement environment)
            && environment.ValueKind == JsonValueKind.Object
            && environment.TryGetProperty("hostId", out JsonElement id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() : null;
    }

    private static CasArtifact ToArtifact(OperationArtifactInfo item)
    {
        if (item.Size < 0 || string.IsNullOrWhiteSpace(item.ArtifactId) ||
            string.IsNullOrWhiteSpace(item.RelativePath))
        {
            throw new InvalidDataException("Invalid artifact identity in operation catalog.");
        }

        string mediaType = Path.GetExtension(item.RelativePath).ToUpperInvariant() switch
        {
            ".JSON" => "application/json",
            ".JSONL" => "application/x-ndjson",
            ".LOG" or ".TXT" => "text/plain",
            ".HTML" => "text/html",
            ".SVG" => "image/svg+xml",
            _ => "application/octet-stream"
        };
        return new(item.ArtifactId, item.Kind, item.RelativePath, Digest(item.Sha256), item.Size, mediaType);
    }

    private static string OperationId(string value) =>
        value.Length == 32 && value.All(Uri.IsHexDigit)
            ? Convert.ToHexStringLower(Convert.FromHexString(value))
            : throw new ArgumentException("Invalid operation ID.");

    private static string Digest(string value) => ArtifactStoreFiles.NormalizeDigest(value);
}
