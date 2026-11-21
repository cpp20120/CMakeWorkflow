namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>One regular file in a bounded, source-only experiment upload.</summary>
public sealed record ExperimentSourceFile(string Path, string Base64);

/// <summary>Exact manifest text and source files to publish under a workspace-owned experiment ID.</summary>
public sealed record ExperimentStageRequest(string ExperimentId, string SourceSnapshotId,
    string ManifestSha256, string Manifest, IReadOnlyList<ExperimentSourceFile> Files)
{
    public ExperimentSourceBundle? Bundle { get; init; }

    public const int MaximumSourceBytes = 8 * 1024 * 1024;
    public const int MaximumFiles = 2048;
    public const int MaximumManifestCharacters = 262144;
}

/// <summary>Verified remote paths and content identities. Staging does not admit execution.</summary>
public sealed record ExperimentStageReceipt(string Manifest, string ManifestSha256, string SourceSnapshotId);

/// <summary>Identity and limits of an experiment ZIP upload.</summary>
public sealed record ExperimentSourceBundle(string Sha256, long Size, int FileCount)
{
    public const int MaximumFiles = 16384;
    public const long MaximumSourceBytes = 512L * 1024 * 1024;
    public const long MaximumArchiveBytes = MaximumSourceBytes + (40L * 1024 * 1024);
    public const int MaximumFileBytes = 128 * 1024 * 1024;
    // ZIP external attributes: Unix regular-file type and 0644/0755 permissions.
    public const int RegularFile = unchecked((int)0x81A40000);
    public const int ExecutableFile = unchecked((int)0x81ED0000);
    public const int ChunkBytes = 512 * 1024;
}

/// <summary>Append a bounded source chunk or query the saved offset with empty data.</summary>
public sealed record ExperimentSourceChunk(string ExperimentId, string Sha256, long Size, long Offset, ReadOnlyMemory<byte> Data);
