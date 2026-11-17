using System.Text.Json;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Core.Persistence;

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

        CasArtifact[] entries = [.. catalog.Artifacts.Select(ToArtifact)
            .OrderBy(a => a.ArtifactId, StringComparer.Ordinal)];
        if (entries.Select(a => a.ArtifactId).Distinct(StringComparer.Ordinal).Count() != entries.Length)
        {
            throw new InvalidDataException("Duplicate artifact ID in finalized catalog.");
        }

        using FileStream lease = await files.OpenLeaseAsync(true, ct);
        string reference = Reference(id);
        CasOperation? existing = File.Exists(reference) ? await ReadAsync(id, ct) : null;
        if (existing is not null && (existing.WorkspaceId != snapshot.WorkspaceId
            || existing.ReportDigest != reportDigest || !existing.Artifacts.SequenceEqual(entries)))
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
            CasOperation receipt = new(1, snapshot.WorkspaceId, id, hostId, reportDigest, true, entries);
            if (existing is not { Pinned: true })
            {
                await WriteAsync(receipt, ct);
            }
            return receipt;
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    internal async Task<CasOperation> ShowAsync(string operation, CancellationToken ct)
    {
        using FileStream lease = await files.OpenLeaseAsync(false, ct);
        return await ReadAsync(OperationId(operation), ct);
    }

    internal async Task<CasOperation> PinAsync(string operation, bool pinned, CancellationToken ct)
    {
        using FileStream lease = await files.OpenLeaseAsync(false, ct);
        CasOperation current = await ReadAsync(OperationId(operation), ct);
        if (pinned)
        {
            foreach (CasArtifact artifact in current.Artifacts.DistinctBy(a => a.Digest))
            {
                await ArtifactStoreFiles.VerifyAsync(files.Blob(artifact.Digest), artifact.Digest, artifact.Size, ct);
            }
        }

        CasOperation updated = current with { Pinned = pinned };
        if (current.Pinned != pinned)
        {
            await WriteAsync(updated, ct);
        }
        return updated;
    }

    internal async Task FetchAsync(string operation, string artifactId, string destination, CancellationToken ct)
    {
        using FileStream lease = await files.OpenLeaseAsync(false, ct);
        CasOperation reference = await ReadAsync(OperationId(operation), ct);
        CasArtifact entry = reference.Artifacts.SingleOrDefault(a => a.ArtifactId == artifactId)
                          ?? throw new KeyNotFoundException("Unknown artifact for this operation.");
        await files.RestoreAsync(entry.Digest, entry.Size, destination, ct);
    }

    internal async Task<CasGcResult> CollectAsync(bool apply, CancellationToken ct)
    {
        using FileStream lease = await files.OpenLeaseAsync(false, ct);
        HashSet<string> reachable = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(files.Operations, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            CasOperation operation = await ReadAsync(OperationId(Path.GetFileNameWithoutExtension(path)), ct);
            if (operation.Pinned)
            {
                reachable.UnionWith(operation.Artifacts.Select(a => a.Digest));
            }
        }

        foreach (string path in Directory.EnumerateFiles(files.Actions, "*.json"))
        {
            string key = Path.GetFileNameWithoutExtension(path);
            CasActionResult action = await CasActions.ReadAsync(files, key, ct);
            _ = reachable.Add(action.Digest);
        }

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

        return new(apply, reachable.Count, count, size);
    }

    private string Reference(string operation) => Path.Combine(files.Operations, operation + ".json");

    private async Task<CasOperation> ReadAsync(string operation, CancellationToken ct)
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

    private Task WriteAsync(CasOperation receipt, CancellationToken ct) =>
        DurableByteWriter.WriteAsync(Reference(receipt.OperationId),
            JsonSerializer.SerializeToUtf8Bytes(receipt, ArtifactCasJson.Default.CasOperation), ct);

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
