using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CMakeWorkflow.Client.Workflows;

/// <summary>
/// Local, operation-bound artifact download with verified restart checkpoints.
/// A failed connection never causes an implicit RPC reconnect or workflow start.
/// </summary>
internal static class OperationArtifactDownloader
{
    private const int ChunkBytes = 64 * 1024;
    private const long CheckpointInterval = 1024 * 1024;
    private const string CheckpointVersion = "cmwf-download-v1";
    private static readonly string EmptyHash = Convert.ToHexString(SHA256.HashData([]));

    internal static async Task<string> DownloadAsync(WorkflowOperation operation, OperationArtifactInfo artifact,
        string destinationPath, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (string.IsNullOrWhiteSpace(artifact.ArtifactId) || artifact.Size < 0 ||
            artifact.LastWriteUtcTicks < 0 || artifact.Sha256.Length != 64 ||
            !artifact.Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid artifact catalog entry.", nameof(artifact));
        }

        ct.ThrowIfCancellationRequested();

        string target = Path.GetFullPath(destinationPath);
        string partial = target + ".cmwf.part";
        string checkpointFile = target + ".cmwf.part.meta";
        string identity = Identity(operation.OperationId, artifact);
        if (!Directory.Exists(Path.GetDirectoryName(target)))
        {
            throw new DirectoryNotFoundException("The download destination directory does not exist.");
        }

        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException("Refusing to overwrite an existing download destination: " + target);
        }

        if (Directory.Exists(partial) || Directory.Exists(checkpointFile))
        {
            throw new IOException("Download state path is a directory.");
        }
        // Do not follow a pre-existing local symlink to an unrelated file when
        // truncating the uncommitted tail or replacing a checkpoint.
        foreach (string? path in new[] { partial, checkpointFile })
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Linked download state is not supported: " + path);
            }
        }

        bool hasPartial = File.Exists(partial);
        bool hasCheckpoint = File.Exists(checkpointFile);
        if (hasPartial != hasCheckpoint)
        {
            throw new InvalidDataException("Orphaned download state; remove the .cmwf.part and .meta files explicitly.");
        }

        if (!hasPartial)
        {
            // Both files are exclusively SDK-owned; do not truncate an existing partial.
            await using (FileStream created = new(partial, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, ChunkBytes, FileOptions.Asynchronous)) { }
            try
            {
                await SaveCheckpointAsync(checkpointFile, identity, 0, EmptyHash, ct);
            }
            catch
            {
                File.Delete(partial);
                throw;
            }
        }

        Checkpoint previous = await ReadCheckpointAsync(checkpointFile, ct);
        if (!string.Equals(previous.Identity, identity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Download checkpoint belongs to another operation or artifact.");
        }

        if (previous.Offset < 0 || previous.Offset > artifact.Size ||
            previous.PrefixSha256.Length != 64 || !previous.PrefixSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Invalid download checkpoint range or digest.");
        }

        try
        {
            await using FileStream output = new(partial, FileMode.Open, FileAccess.ReadWrite,
                             FileShare.None, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (output.Length < previous.Offset)
            {
                throw new InvalidDataException("Download partial is shorter than its committed checkpoint.");
            }
            // A crash can leave uncommitted bytes after the last durable checkpoint.
            if (output.Length > previous.Offset)
            {
                output.SetLength(previous.Offset);
            }

            byte[] buffer = new byte[ChunkBytes];
            long verified = 0;
            while (verified < previous.Offset)
            {
                ct.ThrowIfCancellationRequested();
                int read = await output.ReadAsync(buffer.AsMemory(0,
                    (int)Math.Min(buffer.Length, previous.Offset - verified)), ct);
                if (read <= 0)
                {
                    throw new InvalidDataException("Download partial ended while hashing.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                verified += read;
            }
            if (!string.Equals(Convert.ToHexString(hash.GetCurrentHash()), previous.PrefixSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Download partial does not match its verified checkpoint.");
            }

            long offset = previous.Offset;
            long checkpointed = offset;
            while (offset < artifact.Size)
            {
                ct.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(ChunkBytes, artifact.Size - offset);
                OperationArtifactChunk chunk = await operation.ReadArtifactChunkAsync(artifact, offset, requested, ct);
                byte[]? bytes = chunk.Content;
                if (bytes is null || chunk.ArtifactId != artifact.ArtifactId || chunk.Offset != offset ||
                    chunk.TotalSize != artifact.Size || chunk.LastWriteUtcTicks != artifact.LastWriteUtcTicks ||
                    bytes.Length is 0 || bytes.Length > requested ||
                    chunk.Eof != (offset + bytes.Length == artifact.Size))
                {
                    throw new InvalidDataException("Remote artifact chunk has inconsistent identity, range or EOF.");
                }

                await output.WriteAsync(bytes, ct);
                hash.AppendData(bytes);
                offset += bytes.Length;
                if (offset - checkpointed >= CheckpointInterval || offset == artifact.Size)
                {
                    // Persist bytes BEFORE committing the resumable offset. On a crash,
                    // uncommitted tail bytes are truncated and verified on the next call.

#pragma warning disable CA1849 // Flush(true) must commit bytes to disk; FlushAsync only flushes buffers and cannot replace this durability boundary.
                    output.Flush(flushToDisk: true);
#pragma warning restore CA1849

                    await SaveCheckpointAsync(checkpointFile, identity, offset,
                        Convert.ToHexString(hash.GetCurrentHash()), ct);
                    checkpointed = offset;
                    progress?.Report(offset);
                }
            }

            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), artifact.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Downloaded artifact SHA-256 does not match the remote catalog.");
            }
        }
        catch (InvalidDataException)
        {
            // Invalid bytes must never be resumed or published. Transport failures
            // and cooperative cancellation intentionally preserve verified checkpoints.
            File.Delete(partial);
            File.Delete(checkpointFile);
            throw;
        }

        // Same directory => atomic rename on the same filesystem. Never overwrite
        // a pre-existing destination, including one created during the transfer.
        File.Move(partial, target);
        File.Delete(checkpointFile);
        return target;
    }

    private static string Identity(string operationId, OperationArtifactInfo artifact)
    {
        string input = $"{operationId}\0{artifact.ArtifactId}\0{artifact.Size.ToString(CultureInfo.InvariantCulture)}" +
                    $"\0{artifact.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture)}" +
                    $"\0{artifact.Sha256.ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private sealed record Checkpoint(string Identity, long Offset, string PrefixSha256);

    private static async Task<Checkpoint> ReadCheckpointAsync(string path, CancellationToken ct)
    {
        // Fixed four-line format: no serializer metadata or dynamic RPC types.
        string[] lines = await File.ReadAllLinesAsync(path, ct);
        return lines.Length != 4 || lines[0] != CheckpointVersion ||
            lines[1].Length != 64 || !lines[1].All(Uri.IsHexDigit) ||
            !long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out long offset)
            ? throw new InvalidDataException("Malformed local artifact download checkpoint.")
            : new(lines[1], offset, lines[3]);
    }

    private static async Task SaveCheckpointAsync(string path, string identity, long offset,
        string prefixSha256, CancellationToken ct)
    {
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string data = string.Join("\n", CheckpointVersion, identity,
                offset.ToString(CultureInfo.InvariantCulture), prefixSha256) + "\n";
            await using (FileStream file = new(staging, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(data);
                await file.WriteAsync(bytes, ct);

#pragma warning disable CA1849 // Flush(true) must commit bytes to disk; FlushAsync only flushes buffers and cannot replace this durability boundary.
                file.Flush(flushToDisk: true);
#pragma warning restore CA1849

            }
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }
}
