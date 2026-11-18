namespace CMakeWorkflow.Sources;

public sealed record GitSourceCapture(string SnapshotId, GitSourceSelection Git);

/// <summary>Captures a Git-selected working tree without modifying the user's Git repository.</summary>
public sealed class SourceCaptureService(ISourceSnapshotStore store, GitSourceSelector selector)
{
    private readonly ISourceSnapshotStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly GitSourceSelector _selector = selector ?? throw new ArgumentNullException(nameof(selector));

    public async Task<GitSourceCapture> CaptureGitAsync(string sourceRoot, bool includeUntracked = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        GitSourceSelection selected = await _selector.SelectAsync(sourceRoot, includeUntracked, ct);
        if (selected.Files.Count == 0)
        {
            throw new InvalidDataException("Git selection contains no existing regular files.");
        }
        ct.ThrowIfCancellationRequested();
        string snapshotId = await _store.CaptureAsync(selected.RepositoryRoot, selected.Files, ct);
        return new GitSourceCapture(snapshotId, selected);
    }
}
