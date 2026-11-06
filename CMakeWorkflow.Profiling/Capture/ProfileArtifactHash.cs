using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Core;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Capture;
#pragma warning restore IDE0130

public static class ProfileArtifactHash
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            return await CommandExecution.Sha256Async(path, ct);
        }

        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException("Profile artifact is missing.", path);
        }

        using IncrementalHash tree = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string? file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(path, file).Replace('\\', '/');
            string fileHash = await CommandExecution.Sha256Async(file, ct);
            tree.AppendData(Encoding.UTF8.GetBytes(relative + "\0" + fileHash + "\n"));
        }


#pragma warning disable CA1308 // Lowercase values are part of the existing persisted hash, identifier, or command protocol.
        return Convert.ToHexString(tree.GetHashAndReset()).ToLowerInvariant();
#pragma warning restore CA1308

    }
}