using System.Security.Cryptography;
using System.Text;

namespace CMakeWorkflow.Core.Persistence;

internal sealed class ArtifactStoreFiles(string directory)
{
    private const string Format = "cmakeworkflow-artifact-cas-v1";
    internal string Root { get; } = Path.GetFullPath(directory);
    internal string Blobs => Path.Combine(Root, "blobs");
    internal string Operations => Path.Combine(Root, "operations");
    internal string Actions => Path.Combine(Root, "actions");
    internal string Staging => Path.Combine(Root, "staging");

    internal string Blob(string digest) => Path.Combine(Blobs, NormalizeDigest(digest));

    internal async Task<FileStream> OpenLeaseAsync(bool create, CancellationToken ct)
    {
        if (create)
        {
            _ = Directory.CreateDirectory(Root);
        }

        string lockPath = Path.Combine(Root, ".lock");
        FileMode mode = create ? FileMode.OpenOrCreate : FileMode.Open;
        FileStream lease = await AcquireAsync(lockPath, mode, ct);
        try
        {
            string marker = Path.Combine(Root, ".cas-format");
            if (create && !File.Exists(marker))
            {
                if (Directory.EnumerateFileSystemEntries(Root).Any(path =>
                        Path.GetFileName(path) is not (".lock" or ".cas-format.tmp")))
                {
                    throw new InvalidDataException("Choose an empty directory for a new artifact CAS.");
                }

                await DurableByteWriter.WriteAsync(marker, Encoding.UTF8.GetBytes(Format), ct);
            }

            if (!File.Exists(marker) || new FileInfo(marker).Length != Format.Length
                || await File.ReadAllTextAsync(marker, ct) != Format)
            {
                throw new InvalidDataException("Unrecognized CMakeWorkflow artifact CAS format.");
            }

            if (create)
            {
                _ = Directory.CreateDirectory(Blobs);
                _ = Directory.CreateDirectory(Operations);
                _ = Directory.CreateDirectory(Actions);
                _ = Directory.CreateDirectory(Staging);
            }

            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    private static async Task<FileStream> AcquireAsync(string path, FileMode mode, CancellationToken ct)
    {
        System.Diagnostics.Stopwatch waiting = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, mode, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33
                                            && waiting.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50, ct);
            }
        }
    }

    internal static async Task VerifyAsync(string path, string digest, long size, CancellationToken ct)
    {
        await using FileStream file = OpenSequentialRead(path);
        if (file.Length != size || Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)) != digest)
        {
            throw new InvalidDataException($"CAS integrity verification failed for SHA-256 {digest}.");
        }
    }

    internal async Task RestoreAsync(string digest, long size, string destination, CancellationToken ct)
    {
        string output = Path.GetFullPath(destination);
        string relative = Path.GetRelativePath(Root, output);
        if (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("The fetch destination must be outside the CAS.");
        }

        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await CopyAsync(Blob(digest), temporary, ct);
            await VerifyAsync(temporary, digest, size, ct);
            ct.ThrowIfCancellationRequested();
            DurableByteWriter.PublishNewFile(temporary, output);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        await using FileStream input = OpenSequentialRead(source);
        await using FileStream result = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous);
        await input.CopyToAsync(result, ct);
        await result.FlushAsync(ct);
        RandomAccess.FlushToDisk(result.SafeFileHandle);
    }

    private static FileStream OpenSequentialRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal static string NormalizeDigest(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit)
            ? Convert.ToHexStringLower(Convert.FromHexString(value))
            : throw new ArgumentException("Invalid SHA-256 digest.");

    internal static bool IsCanonicalHex(string? value, int length) =>
        value is not null && value.Length == length && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
