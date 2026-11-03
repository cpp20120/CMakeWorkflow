using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Profiling.Backends;

namespace CMakeWorkflow.Profiling;

public static class ProfileArtifactHash
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        if (File.Exists(path)) return await CommandExecution.Sha256Async(path, ct);
        if (!Directory.Exists(path)) throw new FileNotFoundException("Profile artifact is missing.", path);
        using var tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            var fileHash = await CommandExecution.Sha256Async(file, ct);
            tree.AppendData(Encoding.UTF8.GetBytes(relative + "\0" + fileHash + "\n"));
        }

        return Convert.ToHexString(tree.GetHashAndReset()).ToLowerInvariant();
    }
}