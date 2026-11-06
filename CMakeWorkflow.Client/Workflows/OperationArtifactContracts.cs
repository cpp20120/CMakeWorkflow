using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Workflows;

// Same closed wire shape as the Host DTOs; the SDK does not depend on Host assemblies.
/// <summary>Internal RPC request for the operation handle's finalized artifact catalog lookup.</summary>
/// <param name="OperationId">Operation in the initialized workspace.</param>
internal sealed record OperationArtifactListRequest(string OperationId);
/// <summary>Internal RPC byte-range request constructed from public artifact catalog evidence.</summary>
/// <param name="OperationId">Catalog operation ID.</param>
/// <param name="ArtifactId">Opaque catalog artifact ID.</param>
/// <param name="Offset">Nonnegative byte offset.</param>
/// <param name="Count">Requested bytes, 1..65536.</param>
/// <param name="ExpectedSize">Catalog file size in bytes.</param>
/// <param name="ExpectedLastWriteUtcTicks">Catalog last-write UTC timestamp in .NET ticks (100 nanoseconds).</param>
internal sealed record OperationArtifactChunkRequest(
    string OperationId, string ArtifactId, long Offset, int Count,
    long ExpectedSize, long ExpectedLastWriteUtcTicks);
/// <summary>Finalized file evidence; artifact IDs are opaque and scoped to the operation.</summary>
/// <param name="ArtifactId">Opaque ID for chunk lookup.</param>
/// <param name="Kind">Catalog file category.</param>
/// <param name="NodeId">Producing node ID, or null for operation-wide files.</param>
/// <param name="RelativePath">Host results-relative catalog path; not a local destination.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="LastWriteUtcTicks">Last-write UTC timestamp in .NET ticks (100 nanoseconds), used as change evidence.</param>
/// <param name="Sha256">SHA-256 of the complete file, as hexadecimal characters.</param>
public sealed record OperationArtifactInfo(
    string ArtifactId, string Kind, string? NodeId, string RelativePath,
    long Size, long LastWriteUtcTicks, string Sha256);
/// <summary>Catalog of verified finalized run files.</summary>
/// <param name="OperationId">Owning operation.</param>
/// <param name="ReportSha256">Report hash binding this catalog to durable finalization.</param>
/// <param name="Artifacts">Non-null catalog entries.</param>
[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.Workflows.OperationArtifactList(Artifacts) participates in CMakeWorkflow.Client.Workflows.WorkflowOperation.ListArtifactsAsync; retain these concrete collection parameter types.")]
public sealed record OperationArtifactList(
    string OperationId, string ReportSha256, [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.Workflows.OperationArtifactList.Artifacts (List<OperationArtifactInfo>) is exposed through CMakeWorkflow.Client.Workflows.WorkflowOperation.ListArtifactsAsync; retain this Client SDK property signature.")]
List<OperationArtifactInfo> Artifacts);
/// <summary>One byte range; callers must verify the assembled file against the catalog SHA-256.</summary>
/// <param name="ArtifactId">Catalog artifact identity.</param>
/// <param name="Offset">Starting byte offset of Content.</param>
/// <param name="TotalSize">Complete Host file size in bytes.</param>
/// <param name="LastWriteUtcTicks">Observed last-write UTC timestamp in .NET ticks (100 nanoseconds).</param>
/// <param name="Content">Non-null byte array; may be empty at end of file.</param>
/// <param name="Eof">Whether this range reaches the end of the file.</param>
public sealed record OperationArtifactChunk(
    string ArtifactId, long Offset, long TotalSize, long LastWriteUtcTicks,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
byte[] Content
#pragma warning restore CA1819
, bool Eof);
