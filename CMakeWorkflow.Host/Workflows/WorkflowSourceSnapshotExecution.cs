using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Sources;

namespace CMakeWorkflow.Host.Workflows;

/// <summary>Materializes an accepted source snapshot into operation-owned CMake paths before dispatch.</summary>
/// <remarks>
/// P4.2 intentionally supports only source-rooted managed CMake graphs. It is not
/// a sandbox for arbitrary CMake code: absolute paths embedded in CMake scripts,
/// external dependencies and network access are not made hermetic by this adapter.
/// Recovery reuses operation-owned paths from the accepted snapshot.
/// </remarks>
internal static class WorkflowSourceSnapshotExecution
{
    internal static async Task<WorkflowPlan> PlanAsync(
        WorkspaceReadPaths paths, string operationId, string originalManifest,
        string resultsDirectory, byte[] acceptedManifest, string sourceSnapshotId, IReadOnlyList<string> only,
        CancellationToken ct, bool resume = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(acceptedManifest);
        ArgumentNullException.ThrowIfNull(only);
        ct.ThrowIfCancellationRequested();

        WorkflowDocument document = JsonSerializer.Deserialize(acceptedManifest, WorkflowJson.Default.WorkflowDocument)
            ?? throw new InvalidDataException("Empty accepted workflow manifest.");
        if (document.Contexts is null || document.Contexts.Count == 0 ||
            document.Nodes is null || document.Nodes.Count == 0)
        {
            throw new InvalidDataException("Source-bound workflow requires contexts and nodes.");
        }

        // The accepted manifest remains the authority for original source paths.
        // This mapping must never consult current CMakeCache or source file contents.
        string manifestBase = Path.GetDirectoryName(originalManifest)!;
        string? originalSource = null;
        foreach ((string name, WorkflowContext? context) in document.Contexts)
        {
            if (context is null || string.IsNullOrWhiteSpace(context.SourceDirectory) ||
                string.IsNullOrWhiteSpace(context.BuildDirectory) ||
                context.Mode is not (null or "managed"))
            {
                throw new InvalidOperationException(
                    $"source_snapshot_requires_managed_context: {name}");
            }

            string requested = Path.IsPathFullyQualified(context.SourceDirectory)
                ? context.SourceDirectory : Path.Combine(manifestBase, context.SourceDirectory);
            string canonical = paths.Require(requested);
            if (originalSource is not null && !WorkflowPlanner.PathComparer.Equals(originalSource, canonical))
            {
                throw new InvalidOperationException("source_snapshot_multiple_source_roots_unsupported");
            }
            originalSource = canonical;
        }

        // Inputs and outputs rooted at the ORIGINAL manifest, custom working directories
        // and buildless replay cannot be redirected safely without a separate mapping.
        // Refuse them instead of silently reading the live workspace.
        foreach (WorkflowNode? node in document.Nodes)
        {
            if (node is null || node.Kind == "profile_snapshot" || node.WorkingDirectory is not null ||
                node.Inputs is null || node.Outputs is null ||
                node.Inputs.Any(input => input is null || input.Path is not null) ||
                node.Outputs.Any(output => output is null || output.Root == "manifest"))
            {
                throw new InvalidOperationException("source_snapshot_unmapped_manifest_path");
            }
        }

        // Keep both source and build trees short: CMake/Ninja create deep
        // CMakeFiles/CMakeScratch/TryCompile paths on Windows.
        string operationPath = Path.Combine(paths.WorkspaceDirectory, ".cw", operationId);
        string executionRoot = paths.Require(operationPath);
        string sourcePath = SourcePath(paths.WorkspaceDirectory, operationId);
        string materialized = paths.Require(sourcePath);
        string buildPath = Path.Combine(executionRoot, "b");
        string buildRoot = paths.Require(buildPath);
        if (!WorkflowPlanner.PathComparer.Equals(executionRoot, operationPath) ||
            !WorkflowPlanner.PathComparer.Equals(materialized, sourcePath) ||
            !WorkflowPlanner.PathComparer.Equals(buildRoot, buildPath))
        {
            throw new InvalidOperationException("source_snapshot_execution_path_redirected");
        }

        string results = paths.Require(resultsDirectory);
        string relativeResults = Path.GetRelativePath(executionRoot, results);
        if (relativeResults == "." || relativeResults == ".." ||
            (!Path.IsPathRooted(relativeResults) &&
             !relativeResults.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("source_snapshot_results_overlap_materialization");
        }
        if (!resume && Directory.Exists(buildRoot) && Directory.EnumerateFileSystemEntries(buildRoot).Any())
        {
            throw new InvalidOperationException("source_snapshot_build_tree_not_fresh");
        }

        // Read/verify the Lore manifest and every blob before touching the runner
        // checkpoint or authorizing external execution. No live-tree fallback.
        LoreSnapshotStore lore = new(SourceStorePaths.ForWorkspace(paths.WorkspaceDirectory));
        bool hasMaterialization = Directory.Exists(materialized);
        SnapshotManifest sourceManifest = resume && hasMaterialization
            ? await lore.VerifyAsync(sourceSnapshotId, ct)
            : await lore.InspectAsync(sourceSnapshotId, ct);
        if (!sourceManifest.Files.Any(file => file.Path == "CMakeLists.txt"))
        {
            throw new InvalidDataException("Source snapshot does not contain CMakeLists.txt at its root.");
        }
        if (!hasMaterialization)
        {
            await lore.MaterializeAsync(sourceSnapshotId, materialized, ct);
        }
        else
        {
            await VerifyExistingMaterializationAsync(sourceManifest, materialized, ct);
        }
        ct.ThrowIfCancellationRequested();

        // Every context receives a fresh, operation-scoped build path. Assign
        // collision-free, deterministic indices instead of 64-character hashes:
        // accepted manifest bytes and ordinal key order define the mapping.
        int contextIndex = 0;
        foreach (string name in document.Contexts.Keys.Order(StringComparer.Ordinal))
        {
            WorkflowContext context = document.Contexts[name];
            context.SourceDirectory = materialized;
            context.BuildDirectory = paths.Require(Path.Combine(buildRoot,
                contextIndex.ToString(CultureInfo.InvariantCulture)));
            context.Mode = "managed";
            contextIndex++;
        }

        byte[] rewritten = JsonSerializer.SerializeToUtf8Bytes(document, WorkflowJson.Default.WorkflowDocument);
        // The manifest has been durably snapshotted; do not revalidate live
        // manifest file metadata after acceptance (it may have changed/deleted).
        void GuardPlannedPath(string path)
        {
            if (!WorkflowPlanner.PathComparer.Equals(path, originalManifest))
            {
                paths.Read(path);
            }
        }
        WorkflowPlan plan = await WorkflowPlanner.LoadSnapshotAsync(originalManifest,
            rewritten, only, null, ct, GuardPlannedPath);
        await VerifyExistingMaterializationAsync(sourceManifest, materialized, ct);

        // Preserve the *accepted* manifest identity, not the transformed in-memory
        // document's digest. The transformed document is never admitted as new input.
        string acceptedHash = Convert.ToHexString(SHA256.HashData(acceptedManifest));
        return plan with { ManifestHash = acceptedHash };
    }

    internal static string SourcePath(string workspace, string operationId) =>
        Path.Combine(workspace, ".cw", operationId, "s");

    internal static async Task VerifyExistingMaterializationAsync(
        SnapshotManifest manifest, string root, CancellationToken ct)
    {
        // An accepted-but-not-dispatched operation may be restarted after materialization.
        // Reuse its source only after proving exact content, never by path existence alone.
        HashSet<string> expected = new(WorkflowPlanner.PathComparer);
        HashSet<string> expectedDirectories = new(WorkflowPlanner.PathComparer);
        foreach (SnapshotFile entry in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            _ = expected.Add(entry.Path);
            string[] segments = entry.Path.Split('/');
            for (int i = 1; i < segments.Length; i++)
            {
                _ = expectedDirectories.Add(string.Join("/", segments.Take(i)));
            }
            string file = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            string walk = root;
            foreach (string segment in entry.Path.Split('/'))
            {
                walk = Path.Combine(walk, segment);
                if ((!File.Exists(walk) && !Directory.Exists(walk)) ||
                    (File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Materialized source is missing or redirected: " + entry.Path);
                }
            }
            if (!File.Exists(file))
            {
                throw new InvalidDataException("Materialized source is missing: " + entry.Path);
            }
            await using FileStream input = File.OpenRead(file);
            if (input.Length != entry.Size || SnapshotFile.IsExecutable(file) != entry.Executable ||
                !Convert.ToHexString(await SHA256.HashDataAsync(input, ct))
                    .Equals(entry.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Materialized source changed: " + entry.Path);
            }
        }
        HashSet<string> actual = new(WorkflowPlanner.PathComparer);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            _ = actual.Add(relative);
        }
        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException("Materialized source contains extra or missing files.");
        }
        HashSet<string> actualDirectories = new(WorkflowPlanner.PathComparer);
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Materialized source contains a redirected directory.");
            }
            _ = actualDirectories.Add(Path.GetRelativePath(root, directory)
                .Replace(Path.DirectorySeparatorChar, '/'));
        }
        if (!actualDirectories.SetEquals(expectedDirectories))
        {
            throw new InvalidDataException("Materialized source contains extra or missing directories.");
        }
    }
}
