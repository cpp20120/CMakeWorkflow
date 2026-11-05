#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
using CMakeWorkflow.CMake.Build;

namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

// The first API slice allows only the initialized workspace. Additional roots
// must be a future explicit server policy, never supplied by a request to itself.
internal sealed class WorkspaceReadPaths(string workspace)
{
    internal string WorkspaceDirectory { get; } = BuildTreeOwnership.CanonicalPath(workspace);

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

    public void Read(string path)
    {
        string full = Require(path);
        if (File.Exists(full) && new FileInfo(full).Length > 32 * 1024 * 1024)
        {
            throw new ApplicationRefusal("resource_limit", "Metadata exceeds the 32 MiB read limit.");
        }
    }

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