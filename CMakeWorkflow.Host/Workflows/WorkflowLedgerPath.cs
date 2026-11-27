using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.Host.Sessions;

namespace CMakeWorkflow.Host.Workflows;

internal static class WorkflowLedgerPath
{
    private static string LegacyPath(string workspace) => Path.Combine(workspace, ".cmakeworkflow", "operations.sqlite");

    internal static string Resolve(string workspace)
    {
        string legacy = LegacyPath(workspace);
        if (!Directory.Exists(legacy))
        {
            return legacy;
        }

        RequireUnredirected(legacy);
        return Path.Combine(legacy, "execution-v2.sqlite");
    }

    internal static bool IsBinary(string workspace, string database) =>
        !WorkflowPlanner.PathComparer.Equals(LegacyPath(workspace), database);

    internal static void Fence(string workspace)
    {
        string legacy = LegacyPath(workspace);
        RequireUnredirected(legacy);
        if (File.Exists(legacy))
        {
            throw new InvalidOperationException("binary_workspace_requires_fresh_operation_ledger");
        }

        // SQLite's create-new-file and the directory reservation compete for the
        // same pathname. Never move or replace a legacy database.
        _ = Directory.CreateDirectory(legacy);
        RequireUnredirected(legacy);
    }

    internal static string Documents(string workspace) =>
        Path.Combine(workspace, ".cmakeworkflow", "execution-documents");

    internal static bool InBinaryMetadata(string workspace, string path)
    {
        return Within(LegacyPath(workspace), path) || Within(Documents(workspace), path);

        static bool Within(string directory, string candidate)
        {
            string relative = Path.GetRelativePath(directory, candidate);
            return !Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
    }

    private static void RequireUnredirected(string path)
    {
        string workspace = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        if (!WorkflowPlanner.PathComparer.Equals(new WorkspaceReadPaths(workspace).Require(path), path))
        {
            throw new InvalidDataException("Binary operation ledger namespace is redirected.");
        }
    }
}
