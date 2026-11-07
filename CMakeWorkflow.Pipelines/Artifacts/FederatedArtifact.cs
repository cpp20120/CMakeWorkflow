namespace CMakeWorkflow.Pipelines.Artifacts;

/// <summary>Origin of an artifact. HostLocal and HostSsh share the existing Host RPC catalog protocol.</summary>
public enum ArtifactOrigin
{
    HostLocal,
    HostSsh,
    GitHubActions
}

/// <summary>Whether the catalog can bind an artifact to a precise execution or only to its encompassing run.</summary>
public enum ArtifactExecutionBinding
{
    ExactOperation,
    RunOnly
}

/// <summary>What is known about the artifact digest before bytes are downloaded.</summary>
public enum ArtifactDigestEvidence
{
    NotPublished,
    ProviderReported,
    VerifiedLocally
}

/// <summary>
/// Read-only, provider-neutral artifact observation. Names are display text, never filesystem paths.
/// A catalog observation does not confer authority to read arbitrary provider or workspace paths.
/// </summary>
/// <remarks>
/// SourceIdentity must include provider-scoped execution identity: Host identity and child operation ID for
/// Host RPC artifacts, or repository and run ID for GitHub Actions. GitHub run attempt is represented by
/// the frozen pipeline stage, not asserted as part of the artifact source identity. A run-only artifact must
/// not be promoted as attempt-bound evidence. Provider digests are claims until bytes are independently checked.
/// </remarks>
public sealed record FederatedArtifactKey(
    ArtifactOrigin Origin,
    string PipelineOperationId,
    string StageId,
    string SourceIdentity,
    string ArtifactId);

/// <summary>Metadata observed for one immutable identity. It may change or expire without changing the key.</summary>
public sealed record FederatedArtifact(
    FederatedArtifactKey Key,
    string Name,
    long? Size,
    string? Sha256,
    ArtifactDigestEvidence DigestEvidence,
    ArtifactExecutionBinding ExecutionBinding,
    bool Expired,
    DateTimeOffset? ExpiresAt);
