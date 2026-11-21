namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>One regular file in a bounded, source-only experiment upload.</summary>
public sealed record ExperimentSourceFile(string Path, string Base64);

/// <summary>Exact manifest text and source files to publish under a workspace-owned experiment ID.</summary>
public sealed record ExperimentStageRequest(string ExperimentId, string SourceSnapshotId,
    string ManifestSha256, string Manifest, IReadOnlyList<ExperimentSourceFile> Files)
{
    public const int MaximumSourceBytes = 8 * 1024 * 1024;
    public const int MaximumFiles = 2048;
    public const int MaximumManifestCharacters = 262144;
}

/// <summary>Verified remote paths and content identities. Staging does not admit execution.</summary>
public sealed record ExperimentStageReceipt(string Manifest, string ManifestSha256, string SourceSnapshotId);
