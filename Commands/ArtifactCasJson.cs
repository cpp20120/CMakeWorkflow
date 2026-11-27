using System.Text.Json.Serialization;

namespace CMakeWorkflow.Commands;

internal sealed record CasArtifact(string ArtifactId, string Role, string LogicalPath,
    string Digest, long Size, string MediaType);

internal sealed record CasOperation(int SchemaVersion, string WorkspaceId, string OperationId,
    string? HostId, string ReportDigest, bool Pinned, IReadOnlyList<CasArtifact> Artifacts)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtifactManifestBlobId { get; init; }

    public long LogicalBytes => Artifacts.Sum(a => a.Size);
    public long DistinctBytes => Artifacts.DistinctBy(a => a.Digest).Sum(a => a.Size);
}

internal sealed record CasGcResult(bool Applied, long ReferencedBlobs, long CollectableBlobs,
    long CollectableBytes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CasOperation))]
[JsonSerializable(typeof(CasGcResult))]
internal sealed partial class ArtifactCasJson : JsonSerializerContext;
