namespace CMakeWorkflow.Sources;

/// <summary>Canonical, source-content identity independent of Lore's physical addresses.</summary>
public sealed record SnapshotFile(string Path, string Sha256, long Size)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool Executable { get; init; }

    public static bool IsExecutable(string path) => !OperatingSystem.IsWindows() &&
        (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;

    public void ApplyMode(string path)
    {
        if (!OperatingSystem.IsWindows() && Executable)
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute |
                (UnixFileMode)((int)(mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) >> 2));
        }
    }
}

/// <summary>Contents are ordered by ordinal relative path; Git provenance is stored separately.</summary>
public sealed record SnapshotManifest(int SchemaVersion, IReadOnlyList<SnapshotFile> Files);

public interface ISourceSnapshotStore
{
    Task<string> CaptureAsync(string sourceRoot, IReadOnlyCollection<string> relativeFiles, CancellationToken ct = default);
    Task<SnapshotManifest> InspectAsync(string snapshotId, CancellationToken ct = default);
    Task MaterializeAsync(string snapshotId, string destination, CancellationToken ct = default);
}
