using System.IO.Compression;
using System.Security.Cryptography;
using CMakeWorkflow.Pipelines.Artifacts;

namespace CMakeWorkflow.Pipelines.GitHub;

/// <summary>A file extracted from one independently SHA-256-verified GitHub Actions archive.</summary>
public sealed record VerifiedArchiveFile(string RelativePath, long Size, string Sha256);

/// <summary>Immutable local extraction receipt. The digest is checked against provider-reported ZIP bytes.</summary>
/// <remarks>Verified bytes do not establish the GitHub run attempt that produced an artifact.</remarks>
public sealed record GitHubArtifactExtractionReceipt(
    FederatedArtifactKey Key, string DestinationDirectory, string ArchiveSha256,
    IReadOnlyList<VerifiedArchiveFile> Files);

/// <summary>Bounded, no-overwrite extraction of a GitHub Actions artifact ZIP.</summary>
/// <remarks>
/// Input must come from an artifact selected by a verified P5a catalog, not an arbitrary URL.
/// This component performs no HTTP, ledger writes or execution. The caller must obtain the
/// ZIP through the pinned GitHub artifact ID; GitHub digests refer to the ZIP archive bytes.
/// The destination parent must be caller-owned and free of hostile concurrent filesystem writers.
/// </remarks>
public static class GitHubArtifactArchiveVerifier
{
    private const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private const long MaximumExtractedBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumSingleFileBytes = 512L * 1024 * 1024;
    private const int MaximumEntries = 1024;
    private const int BufferBytes = 64 * 1024;

    /// <summary>Verify exact ZIP bytes, then publish a bounded extraction by same-parent atomic rename.</summary>
    /// <remarks>Failure or cancellation leaves the destination absent and removes temporary files.</remarks>
    public static async Task<GitHubArtifactExtractionReceipt> ExtractAsync(
        FederatedArtifact artifact, Stream archiveStream, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(archiveStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (artifact.Key.Origin != ArtifactOrigin.GitHubActions ||
            artifact.DigestEvidence != ArtifactDigestEvidence.ProviderReported ||
            artifact.Sha256 is not { Length: 64 } || !artifact.Sha256.All(char.IsAsciiHexDigit) ||
            artifact.Expired || artifact.ExecutionBinding != ArtifactExecutionBinding.RunOnly)
        {
            throw new InvalidDataException("Artifact must be a non-expired, run-bound GitHub ZIP with a provider SHA-256.");
        }
        if (!archiveStream.CanRead)
        {
            throw new ArgumentException("Archive stream must be readable.", nameof(archiveStream));
        }
        long expectedArchiveSize = artifact.Size ??
            throw new InvalidDataException("GitHub archive catalog has no compressed byte size.");
        if (expectedArchiveSize is < 0 or > MaximumArchiveBytes)
        {
            throw new InvalidDataException("GitHub ZIP catalog size exceeds limits.");
        }

        string destination = Path.GetFullPath(destinationDirectory);
        string parent = Path.GetDirectoryName(destination) ?? throw new IOException("Invalid destination directory.");
        if (!Directory.Exists(parent) || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0 ||
            File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException("Destination must not exist and its parent must be an ordinary existing directory.");
        }
        string nonce = Guid.NewGuid().ToString("N");
        string archivePath = destination + ".cmwf-zip-" + nonce;
        string staging = destination + ".cmwf-tree-" + nonce;
        try
        {
            // Copy with a hard byte cap; never buffer an untrusted ZIP in memory.
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using FileStream output = new(archivePath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, BufferBytes, FileOptions.Asynchronous);
                byte[] buffer = new byte[BufferBytes];
                long size = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await archiveStream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }
                    size = checked(size + read);
                    if (size > MaximumArchiveBytes)
                    {
                        throw new InvalidDataException("GitHub ZIP exceeds the archive byte limit.");
                    }
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                string observed = Convert.ToHexString(hash.GetHashAndReset());
                if (size != expectedArchiveSize ||
                    !string.Equals(observed, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("GitHub ZIP size or SHA-256 does not match catalog evidence.");
                }
            }

            if (File.Exists(staging) || Directory.Exists(staging))
            {
                throw new IOException("Refusing to reuse an existing extraction staging path.");
            }
            _ = Directory.CreateDirectory(staging);
            List<VerifiedArchiveFile> files = [];
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            await using (FileStream source = new(archivePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferBytes, FileOptions.Asynchronous | FileOptions.RandomAccess))
            using (ZipArchive zip = new(source, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (zip.Entries.Count > MaximumEntries)
                {
                    throw new InvalidDataException("GitHub ZIP contains too many entries.");
                }
                long declaredTotal = 0;
                // Check the entire archive before creating any extracted files.
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool directory = entry.FullName.EndsWith('/');
                    string relative = ValidateName(entry.FullName, directory);
                    if (!names.Add(relative))
                    {
                        throw new InvalidDataException("Duplicate or case-colliding ZIP entry.");
                    }
                    int fileType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (fileType is not (0 or 0x8000 or 0x4000) ||
                        (directory && fileType == 0x8000) || (!directory && fileType == 0x4000))
                    {
                        throw new InvalidDataException("Non-regular ZIP entry or symlink is refused.");
                    }
                    if (directory)
                    {
                        if (entry.Length != 0)
                        {
                            throw new InvalidDataException("ZIP directory contains data.");
                        }
                        continue;
                    }
                    if (entry.Length > MaximumSingleFileBytes ||
                        (entry.Length > 0 && entry.CompressedLength == 0))
                    {
                        throw new InvalidDataException("ZIP file exceeds size limits or has invalid compression.");
                    }
                    declaredTotal = checked(declaredTotal + entry.Length);
                    if (declaredTotal > MaximumExtractedBytes)
                    {
                        throw new InvalidDataException("ZIP extraction exceeds total byte limit.");
                    }
                }
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool directory = entry.FullName.EndsWith('/');
                    string relative = ValidateName(entry.FullName, directory);
                    string outputPath = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!outputPath.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("ZIP entry escapes staging root.");
                    }
                    if (directory)
                    {
                        _ = Directory.CreateDirectory(outputPath);
                        continue;
                    }
                    _ = Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    using IncrementalHash fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using FileStream target = new(outputPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, BufferBytes, FileOptions.Asynchronous);
                    await using Stream entryStream = await entry.OpenAsync(cancellationToken);
                    byte[] buffer = new byte[BufferBytes];
                    long written = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int read = await entryStream.ReadAsync(buffer, cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }
                        written = checked(written + read);
                        if (written > entry.Length || written > MaximumSingleFileBytes)
                        {
                            throw new InvalidDataException("ZIP content differs from declared uncompressed length.");
                        }
                        fileHash.AppendData(buffer.AsSpan(0, read));
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    if (written != entry.Length)
                    {
                        throw new InvalidDataException("Truncated ZIP entry.");
                    }
                    files.Add(new(relative, written, Convert.ToHexString(fileHash.GetHashAndReset())));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new IOException("Refusing to overwrite destination created during extraction.");
            }
            Directory.Move(staging, destination);
            return new(artifact.Key, destination, artifact.Sha256.ToUpperInvariant(), files.AsReadOnly());
        }
        finally
        {
            // No success path retains the original ZIP; only the verified extraction is published.
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static string ValidateName(string fullName, bool directory)
    {
        string trimmed = directory ? fullName[..^1] : fullName;
        if (trimmed.Length is < 1 or > 1024 || trimmed.StartsWith('/') ||
            trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.Contains(':', StringComparison.Ordinal) ||
            trimmed.Any(static c => char.IsControl(c) || c is '<' or '>' or '"' or '|' or '?' or '*'))
        {
            throw new InvalidDataException("Unsafe ZIP entry name.");
        }
        foreach (string component in trimmed.Split('/'))
        {
            if (component.Length is < 1 or > 255 || component is "." or ".." ||
                component.EndsWith(' ') || component.EndsWith('.') || ReservedDevice(component))
            {
                throw new InvalidDataException("Unsafe ZIP path component.");
            }
        }
        return trimmed;
    }

    private static bool ReservedDevice(string name)
    {
        string stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
             stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }
}
