using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Artifacts;

/// <summary>Collects declared file/tree evidence without claiming atomic filesystem snapshots.</summary>
/// <remarks>
/// Caller owns execution/run leases and post-copy hash verification. Detected symbolic links/reparse
/// points are rejected, but ordinary pathname checks/opens here are not the handle-relative Host artifact API.
/// </remarks>
internal static class WorkflowArtifacts
{
    internal static bool ValidSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static string ManifestPath(string directory, string path) => Path.GetFullPath(
        Path.IsPathFullyQualified(path) ? path : Path.Combine(directory, path));

    internal static string OutputPath(string manifest, string run, WorkflowArtifactOutput output) =>
        output.Root == "run"
            ? Path.GetFullPath(Path.Combine(run, output.Path))
            : ManifestPath(Path.GetDirectoryName(manifest)!, output.Path);

    /// <summary>Hashes a file or an ordinally ordered directory file inventory.</summary>
    /// <remarks>
    /// Directory evidence combines normalized relative file names and file hashes; empty directories are not
    /// included. No lock prevents external mutation during traversal. Returns lowercase SHA-256 and throws
    /// for missing inputs or detected links; callers must decide whether/when to revalidate evidence.
    /// </remarks>
    internal static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            RejectLink(path);
            await using FileStream stream = File.OpenRead(path);

            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));

        }

        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException("Declared artifact is missing.", path);
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Files(path))
        {
            ct.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            string fileHash = await HashAsync(file, ct);
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0" + fileHash + "\n"));
        }


        return Convert.ToHexStringLower(hash.GetHashAndReset());

    }

    /// <summary>Copies declared evidence into an initially absent destination without rollback on interruption.</summary>
    /// <remarks>
    /// Does not itself establish a verified snapshot. Caller must compare the copy hash with the observed
    /// source hash before trusting it. Cancellation/I/O failure can leave a partial destination which subsequent
    /// calls reject until explicitly handled; no atomic publication or durability sync is performed.
    /// </remarks>
    internal static async Task CopySnapshotAsync(string source, string destination, CancellationToken ct)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException("Snapshot destination already exists: " + destination);
        }

        if (File.Exists(source))
        {
            RejectLink(source);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using FileStream input = File.OpenRead(source);
            await using FileStream output = File.Create(destination);
            await input.CopyToAsync(output, ct);
            return;
        }

        if (!Directory.Exists(source))
        {
            throw new FileNotFoundException("Snapshot input is missing.", source);
        }

        _ = Directory.CreateDirectory(destination);
        foreach (string file in Files(source))
        {
            ct.ThrowIfCancellationRequested();
            string copy = Path.Combine(destination, Path.GetRelativePath(source, file));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            await using FileStream input = File.OpenRead(file);
            await using FileStream output = File.Create(copy);
            await input.CopyToAsync(output, ct);
        }
    }

    private static List<string> Files(string root)
    {
        Stack<string> pending = new();
        pending.Push(root);
        List<string> files = [];
        while (pending.TryPop(out string? directory))
        {
            RejectLink(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectLink(entry);
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Declared artifact contains a symbolic link or reparse point: " + path);
        }
    }
}