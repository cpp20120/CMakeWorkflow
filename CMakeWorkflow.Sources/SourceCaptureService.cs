namespace CMakeWorkflow.Sources;

public sealed record GitSourceCapture(string SnapshotId, GitSourceSelection Git);

/// <summary>Captures a Git-selected working tree without modifying the user's Git repository.</summary>
public sealed class SourceCaptureService(ISourceSnapshotStore store, GitSourceSelector selector)
{
    private readonly ISourceSnapshotStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly GitSourceSelector _selector = selector ?? throw new ArgumentNullException(nameof(selector));

    public GitSourceCapture CaptureGit(string sourceRoot, bool includeUntracked = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        GitSourceSelection selected = _selector.Select(sourceRoot, includeUntracked);
        if (selected.Files.Count == 0)
        {
            throw new InvalidDataException("Git selection contains no existing regular files.");
        }
        string snapshotId = _store.Capture(selected.RepositoryRoot, selected.Files);
        return new GitSourceCapture(snapshotId, selected);
    }
}
