using CMakeWorkflow.CMake.Artifacts;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Sources;

namespace CMakeWorkflow.Host.Workflows;

internal sealed record SourceBoundArtifactLayout(string SourcePrefix, IReadOnlyDictionary<string, string> Inputs)
{
    internal const string OutputDirectory = "manifest-artifacts";

    internal static SourceBoundArtifactLayout? Validate(WorkflowDocument document, string manifest, string originalSource,
        SnapshotManifest snapshot)
    {
        if (!document.Nodes.Any(node => node.Inputs.Any(input => input.Path is not null) ||
            node.Outputs.Any(output => output.Root == "manifest")))
        {
            return null;
        }

        string sourcePath = Path.GetRelativePath(Path.GetDirectoryName(manifest)!, originalSource)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (sourcePath != ".")
        {
            SourcePathPolicy.Validate(sourcePath);
        }

        string prefix = sourcePath == "." ? "" : sourcePath + "/";
        Dictionary<string, string> immutableFiles = snapshot.Files.ToDictionary(file => prefix + file.Path,
            file => file.Sha256, StringComparer.Ordinal);
        HashSet<string> immutableDirectories = new(WorkflowPlanner.PathComparer);
        HashSet<string> physicalFiles = new(immutableFiles.Keys, WorkflowPlanner.PathComparer);
        foreach (string file in immutableFiles.Keys)
        {
            AddParents(immutableDirectories, file);
        }
        if (sourcePath != ".")
        {
            _ = immutableDirectories.Add(sourcePath);
        }

        Dictionary<string, string> inputs = new(StringComparer.Ordinal);
        HashSet<string> files = new(WorkflowPlanner.PathComparer);
        HashSet<string> directories = new(WorkflowPlanner.PathComparer);
        foreach (WorkflowNode node in document.Nodes)
        {
            SourceBoundManifestArtifacts.ValidateArguments(node);
            foreach (WorkflowArtifactInput input in node.Inputs)
            {
                if (input.Path is not { } path)
                {
                    continue;
                }

                SourcePathPolicy.Validate(path);
                if (!immutableFiles.TryGetValue(path, out string? hash) ||
                    (input.Sha256 is { } expected && !expected.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"Manifest input must name an exact file in the approved Lore snapshot: {path}");
                }
                inputs[path] = hash;
            }

            foreach (WorkflowArtifactOutput output in node.Outputs)
            {
                SourcePathPolicy.Validate(output.Path);
                if (output.Root == "run")
                {
                    if (output.Path.Split('/')[0].Equals(OutputDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("The manifest artifact directory is reserved for mapped outputs.");
                    }
                    continue;
                }

                string path = output.Path;
                if (!files.Add(path) || directories.Contains(path) || immutableDirectories.Contains(path) ||
                    physicalFiles.Contains(path) || Parents(path).Any(p => files.Contains(p) || physicalFiles.Contains(p)))
                {
                    throw new InvalidDataException($"Manifest output overlaps a source path or another output: {path}");
                }
                AddParents(directories, path);
            }
        }

        return new(prefix, inputs);
    }

    internal SourceBoundManifestArtifacts Map(WorkspaceReadPaths workspace, string source, string run, string snapshotId)
    {
        string output = Path.Combine(run, OutputDirectory);
        void RequireUnredirected(string path)
        {
            if (!WorkflowPlanner.PathComparer.Equals(workspace.Require(path), path))
            {
                throw new InvalidDataException("Source-bound artifact path is redirected: " + path);
            }

            // Reject reparse points that happen to resolve to the same filesystem name.
            for (string? current = path; current is not null && !WorkflowPlanner.PathComparer.Equals(current, workspace.WorkspaceDirectory);
                 current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Source-bound artifact path is a reparse point: " + current);
                }
            }
        }

        RequireUnredirected(output);
        return new(snapshotId, SourcePrefix, source, output, Inputs, RequireUnredirected);
    }

    private static IEnumerable<string> Parents(string path)
    {
        for (int slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/'))
        {
            path = path[..slash];
            yield return path;
        }
    }

    private static void AddParents(HashSet<string> directories, string path) => directories.UnionWith(Parents(path));
}
