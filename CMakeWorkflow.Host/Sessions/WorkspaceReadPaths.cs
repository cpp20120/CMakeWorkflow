// Preserve the established namespace; folders only group implementation.
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Host.Protocol;

namespace CMakeWorkflow.Host.Sessions;
#pragma warning restore IDE0130

// The first API slice allows only the initialized workspace. Additional roots
// must be a future explicit server policy, never supplied by a request to itself.
/// <summary>Applies the initialized workspace boundary to Host filesystem requests.</summary>
/// <remarks>
/// Request data cannot introduce extra allowed roots. Component links are resolved before containment checks,
/// including missing leaves. These pathname checks do not pin files against concurrent replacement; artifact
/// reads requiring that protection use OperationArtifactFile and handle-relative opens instead.
/// </remarks>
internal sealed class WorkspaceReadPaths(string workspace)
{
    internal string WorkspaceDirectory { get; } = BuildTreeOwnership.CanonicalPath(workspace);

    /// <summary>Resolves an absolute Host path and rejects a resolved target outside the workspace.</summary>
    /// <remarks>
    /// Does not require the leaf to exist, create files, acquire a lease or retain an open handle. Filesystem
    /// errors propagate; successful validation is an observation and must not be treated as race-free read authorization.
    /// </remarks>
    /// <param name="path">Absolute path on the Host, never a client-local path translated by the transport.</param>
    /// <returns>Resolved path within the initialized workspace.</returns>
    public string Require(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ApplicationRefusal("invalid_argument", "An absolute host filesystem path is required.");
        }

        string full = Path.GetFullPath(path);
        // Resolve each component, including file symlinks and missing leaves.
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null)
            {
                current = entry.ResolveLinkTarget(true)?.FullName
                          ?? throw new ApplicationRefusal("path_not_allowed", "Unresolved filesystem link.");
            }
        }

        string relative = Path.GetRelativePath(WorkspaceDirectory, current);
        return Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? throw new ApplicationRefusal("path_not_allowed", "Requested path is outside the initialized workspace.")
            : current;
    }

    public bool Contains(string path)
    {
        try
        {
            _ = Require(path);
            return true;
        }
        catch (Exception error) when (error is ApplicationRefusal or IOException or UnauthorizedAccessException
                                          or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Checks workspace containment and the 32 MiB metadata size limit for an existing file.</summary>
    /// <remarks>
    /// Does not read or open the file. A missing file is left for the consumer to handle; directories have no file-size check.
    /// </remarks>
    public void Read(string path)
    {
        string full = Require(path);
        if (File.Exists(full) && new FileInfo(full).Length > 32 * 1024 * 1024)
        {
            throw new ApplicationRefusal("resource_limit", "Metadata exceeds the 32 MiB read limit.");
        }
    }

    /// <summary>Checks an existing database and its SQLite sidecar paths against workspace policy.</summary>
    /// <remarks>
    /// Does not open the database, migrate its schema or make a read-only connection. Consumers must select
    /// the appropriate SQLite open mode; the ordinary metadata file-size cap is not applied here.
    /// </remarks>
    public string Database(string path)
    {
        string full = Require(path);
        foreach (string? suffix in new[] { "-wal", "-shm", "-journal" })
        {
            _ = Require(full + suffix);
        }

        return !File.Exists(full)
            ? throw new ApplicationRefusal("not_found",
                "History database does not exist; import results explicitly with the CLI.")
            : full;
    }
}