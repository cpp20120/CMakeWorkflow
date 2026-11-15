using System.Security.Cryptography;
using System.Text;

namespace CMakeWorkflow.Sources;

/// <summary>Stable Host-local Lore storage path for an absolute workspace identity.</summary>
/// <remarks>
/// Kept outside the workspace because the workspace itself may be a Git source root;
/// capturing an in-tree Lore store would recursively include storage metadata.
/// The caller must pass the canonical workspace path used by the Host ledger.
/// This is local storage, not a portable remote CAS address.
/// </remarks>
public static class SourceStorePaths
{
    /// <summary>Returns a deterministic per-workspace path without creating it.</summary>
    public static string ForWorkspace(string canonicalWorkspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalWorkspace);
        if (!Path.IsPathFullyQualified(canonicalWorkspace))
        {
            throw new ArgumentException("An absolute workspace path is required.", nameof(canonicalWorkspace));
        }

        string data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(data) || !Path.IsPathFullyQualified(data))
        {
            throw new InvalidOperationException("A stable per-user application-data directory is required for source snapshots.");
        }

        string identity = Path.GetFullPath(canonicalWorkspace);
        if (OperatingSystem.IsWindows())
        {
            identity = identity.ToUpperInvariant();
        }
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(data, "CMakeWorkflow", "source-snapshots", key);
    }
}
