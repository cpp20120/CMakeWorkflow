namespace CMakeWorkflow.Host;

// Binary payloads are base64-encoded by the existing JSON-RPC serializer.
public sealed record OperationArtifactListRequest(string OperationId);
public sealed record OperationArtifactChunkRequest(
    string OperationId, string ArtifactId, long Offset, int Count,
    long ExpectedSize, long ExpectedLastWriteUtcTicks);
public sealed record OperationArtifactInfo(
    string ArtifactId, string Kind, string? NodeId, string RelativePath,
    long Size, long LastWriteUtcTicks, string Sha256);
public sealed record OperationArtifactList(
    string OperationId, string ReportSha256, List<OperationArtifactInfo> Artifacts);
public sealed record OperationArtifactChunk(
    string ArtifactId, long Offset, long TotalSize, long LastWriteUtcTicks,
    byte[] Content, bool Eof);
