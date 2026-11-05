using System.Security.Cryptography;
using System.Text;

namespace CMakeWorkflow.CMake.Artifacts;

internal static class WorkflowArtifacts
{
    internal static bool ValidSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static string ManifestPath(string directory, string path) => Path.GetFullPath(
        Path.IsPathFullyQualified(path) ? path : Path.Combine(directory, path));

    internal static string OutputPath(string manifest, string run, WorkflowArtifactOutput output) =>
        output.Root == "run"
            ? Path.GetFullPath(Path.Combine(run, output.Path))
            : ManifestPath(Path.GetDirectoryName(manifest)!, output.Path);

    internal static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            RejectLink(path);
            await using FileStream stream = File.OpenRead(path);

#pragma warning disable CA1308 // Lowercase values are part of the existing persisted hash, identifier, or command protocol.
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
#pragma warning restore CA1308

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


#pragma warning disable CA1308 // Lowercase values are part of the existing persisted hash, identifier, or command protocol.
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
#pragma warning restore CA1308

    }

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