using System.Security.Cryptography;
using System.Text;

namespace CMakeWorkflow;

public sealed record WorkflowArtifactEvidence(
    string Name,
    string Path,
    string Sha256,
    string Role,
    string? From = null,
    string? SnapshotPath = null);

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
            await using var stream = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        }

        if (!Directory.Exists(path)) throw new FileNotFoundException("Declared artifact is missing.", path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Files(path))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            var fileHash = await HashAsync(file, ct);
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0" + fileHash + "\n"));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static async Task CopySnapshotAsync(string source, string destination, CancellationToken ct)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("Snapshot destination already exists: " + destination);
        if (File.Exists(source))
        {
            RejectLink(source);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = File.OpenRead(source);
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, ct);
            return;
        }

        if (!Directory.Exists(source)) throw new FileNotFoundException("Snapshot input is missing.", source);
        Directory.CreateDirectory(destination);
        foreach (var file in Files(source))
        {
            ct.ThrowIfCancellationRequested();
            var copy = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            await using var input = File.OpenRead(file);
            await using var output = File.Create(copy);
            await input.CopyToAsync(output, ct);
        }
    }

    private static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var files = new List<string>();
        while (pending.TryPop(out var directory))
        {
            RejectLink(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectLink(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
                else files.Add(entry);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Declared artifact contains a symbolic link or reparse point: " + path);
    }
}