using System.Diagnostics.CodeAnalysis;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

// Binary payloads are base64-encoded by the existing JSON-RPC serializer.
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
