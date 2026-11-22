namespace CMakeWorkflow.Sources;

public interface ISourceSnapshotStore
{
    Task<string> CaptureAsync(string sourceRoot, IReadOnlyCollection<string> relativeFiles, CancellationToken ct = default);
    Task<SnapshotManifest> InspectAsync(string snapshotId, CancellationToken ct = default);
    Task MaterializeAsync(string snapshotId, string destination, CancellationToken ct = default);
}
