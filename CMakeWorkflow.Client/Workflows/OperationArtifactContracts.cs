using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Workflows;

// Same closed wire shape as the Host DTOs; the SDK does not depend on Host assemblies.
public sealed record OperationArtifactListRequest(string OperationId);
public sealed record OperationArtifactChunkRequest(
    string OperationId, string ArtifactId, long Offset, int Count,
    long ExpectedSize, long ExpectedLastWriteUtcTicks);
public sealed record OperationArtifactInfo(
    string ArtifactId, string Kind, string? NodeId, string RelativePath,
    long Size, long LastWriteUtcTicks, string Sha256);
[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record OperationArtifactList(
    string OperationId, string ReportSha256, [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<OperationArtifactInfo> Artifacts);
public sealed record OperationArtifactChunk(
    string ArtifactId, long Offset, long TotalSize, long LastWriteUtcTicks,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
byte[] Content
#pragma warning restore CA1819
, bool Eof);
