namespace CMakeWorkflow.Host;

internal sealed class ApplicationRefusal(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// The first API slice allows only the initialized workspace. Additional roots
// must be a future explicit server policy, never supplied by a request to itself.
internal sealed class WorkspaceReadPaths(string workspace)
{
    private readonly string root = BuildTreeOwnership.CanonicalPath(workspace);
    internal string WorkspaceDirectory => root;

    public string Require(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ApplicationRefusal("invalid_argument", "An absolute host filesystem path is required.");
        var full = Path.GetFullPath(path);
        // Resolve each component, including file symlinks and missing leaves.
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null)
                current = entry.ResolveLinkTarget(true)?.FullName
                    ?? throw new ApplicationRefusal("path_not_allowed", "Unresolved filesystem link.");
        }
        var relative = Path.GetRelativePath(root, current);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ApplicationRefusal("path_not_allowed", "Requested path is outside the initialized workspace.");
        return current;
    }

    public bool Contains(string path)
    {
        try { Require(path); return true; }
        catch (Exception error) when (error is ApplicationRefusal or IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public void Read(string path)
    {
        var full = Require(path);
        if (File.Exists(full) && new FileInfo(full).Length > 32 * 1024 * 1024)
            throw new ApplicationRefusal("resource_limit", "Metadata exceeds the 32 MiB read limit.");
    }

    public string Database(string path)
    {
        var full = Require(path);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) Require(full + suffix);
        if (!File.Exists(full)) throw new ApplicationRefusal("not_found", "History database does not exist; import results explicitly with the CLI.");
        return full;
    }
}
