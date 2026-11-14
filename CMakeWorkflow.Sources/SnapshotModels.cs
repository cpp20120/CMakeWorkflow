namespace CMakeWorkflow.Sources;

/// <summary>Canonical, source-content identity independent of Lore's physical addresses.</summary>
public sealed record SnapshotFile(string Path, string Sha256, long Size);

/// <summary>Contents are ordered by ordinal relative path; Git provenance is stored separately.</summary>
public sealed record SnapshotManifest(int SchemaVersion, IReadOnlyList<SnapshotFile> Files);

public interface ISourceSnapshotStore
{
    string Capture(string sourceRoot, IReadOnlyCollection<string> relativeFiles);
    SnapshotManifest Inspect(string snapshotId);
    void Materialize(string snapshotId, string destination);
}
