#if EXPERIMENT_SDK
namespace CMakeWorkflow.Client.Experiments;
#else
namespace CMakeWorkflow.Sources;
#endif

/// <summary>Canonical, source-content identity independent of Lore's physical addresses.</summary>
#if EXPERIMENT_SDK
internal
#else
public
#endif
sealed record SnapshotFile(string Path, string Sha256, long Size)
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
#if EXPERIMENT_SDK
internal
#else
public
#endif
sealed record SnapshotManifest(int SchemaVersion, IReadOnlyList<SnapshotFile> Files);
