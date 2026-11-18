namespace CMakeWorkflow.Sources;

/// <summary>Canonical, source-content identity independent of Lore's physical addresses.</summary>
public sealed record SnapshotFile(string Path, string Sha256, long Size);

/// <summary>Contents are ordered by ordinal relative path; Git provenance is stored separately.</summary>
public sealed record SnapshotManifest(int SchemaVersion, IReadOnlyList<SnapshotFile> Files);

public interface ISourceSnapshotStore
{
    Task<string> CaptureAsync(string sourceRoot, IReadOnlyCollection<string> relativeFiles, CancellationToken ct = default);
    Task<SnapshotManifest> InspectAsync(string snapshotId, CancellationToken ct = default);
    Task MaterializeAsync(string snapshotId, string destination, CancellationToken ct = default);
}
