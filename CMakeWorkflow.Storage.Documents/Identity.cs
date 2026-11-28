using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Core.Persistence;

namespace CMakeWorkflow.Storage.Documents;

public readonly record struct BlobId
{
    public BlobId(string value)
    {
        Value = ArtifactStoreFiles.NormalizeDigest(value);
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct CanonicalDigest
{
    public CanonicalDigest(string value)
    {
        Value = ArtifactStoreFiles.NormalizeDigest(value);
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct LegacyDigest
{
    public LegacyDigest(string value)
    {
        Value = ArtifactStoreFiles.NormalizeDigest(value);
    }

    public string Value { get; }
    public override string ToString() => Value;
}

/// <summary>SHA-256 in the domain-separated cmwf-document-v1 logical identity scheme.</summary>
public readonly record struct DocumentId
{
    public const string Scheme = "cmwf-document-v1";

    public DocumentId(string value)
    {
        Value = ArtifactStoreFiles.NormalizeDigest(value);
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public enum DocumentCodec { None, WorkflowJson = 1, WorkflowCapnp = 2, ProductionWorkflowCapnp = 3, ExecutionPlanCapnp = 4, ExecutionReportCapnp = 5 }
public enum DocumentAccess { Buffered, Mapped }
public enum VerificationMode { Full, StoredBytes }

public sealed record DocumentDescriptor(DocumentId Id, DocumentCodec Codec, int SchemaVersion, BlobId Blob,
    CanonicalDigest? Canonical, long StoredLength)
{
    public string Kind => KindFor(Codec);
    public string Schema => SchemaFor(Codec);
    public string IdentityScheme { get; } = DocumentId.Scheme;
    public int DescriptorVersion { get; } = 1;
    public int CodecVersion { get; } = 1;
    public string Normalization => Codec == DocumentCodec.WorkflowJson ? "exact-json" : "official-canonical-segment";
    public LegacyDigest? Legacy => Codec == DocumentCodec.WorkflowJson ? new(Blob.Value) : null;

    internal static DocumentDescriptor Create(DocumentCodec codec, int version, BlobId blob, CanonicalDigest? canonical, long size)
    {
        if (!ArtifactStoreFiles.IsCanonicalHex(blob.Value, 64)
            || (canonical.HasValue && !ArtifactStoreFiles.IsCanonicalHex(canonical.Value.Value, 64))
            || size is < 1 or > WorkflowDocuments.MaxStoredBytes
            || !IsSupported(codec, version, canonical.HasValue, size))
        {
            throw new InvalidDataException("Invalid workflow document descriptor.");
        }

        string identity = string.Create(CultureInfo.InvariantCulture,
            $"{DocumentId.Scheme}\n{KindFor(codec)}\n{SchemaFor(codec)}\n{version}\n{(int)codec}\n1\n{canonical?.Value ?? blob.Value}");
        return new(new(Hash(Encoding.UTF8.GetBytes(identity))), codec, version, blob, canonical, size);
    }

    private static string KindFor(DocumentCodec codec) => codec switch
    {
        DocumentCodec.ExecutionPlanCapnp => "execution-plan",
        DocumentCodec.ExecutionReportCapnp => "execution-report",
        DocumentCodec.None or DocumentCodec.WorkflowJson or DocumentCodec.WorkflowCapnp or DocumentCodec.ProductionWorkflowCapnp => "workflow-manifest",
        _ => throw new ArgumentOutOfRangeException(nameof(codec))
    };

    private static string SchemaFor(DocumentCodec codec) => codec switch
    {
        DocumentCodec.ExecutionPlanCapnp => "cmwf.execution-plan",
        DocumentCodec.ExecutionReportCapnp => "cmwf.execution-report",
        DocumentCodec.ProductionWorkflowCapnp => "cmwf.production-workflow",
        DocumentCodec.None or DocumentCodec.WorkflowJson or DocumentCodec.WorkflowCapnp => "cmwf.workflow",
        _ => throw new ArgumentOutOfRangeException(nameof(codec))
    };

    private static bool IsSupported(DocumentCodec codec, int version, bool canonical, long size) => codec switch
    {
        DocumentCodec.WorkflowJson => version is 1 or 2 && !canonical,
        DocumentCodec.WorkflowCapnp => version == 2 && canonical && size >= 16,
        DocumentCodec.ProductionWorkflowCapnp => version is 1 or 2 && canonical && size >= 16,
        DocumentCodec.ExecutionPlanCapnp or DocumentCodec.ExecutionReportCapnp => version == 1 && canonical && size >= 16,
        DocumentCodec.None => false,
        _ => false
    };

    internal static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}

public sealed record VerificationReport(bool ExactBytes, bool CanonicalSegment, bool Wire, bool SchemaProjection,
    bool Graph);

public sealed record MigrationResult(DocumentDescriptor Legacy, DocumentDescriptor Binary, string NormalizedProjectionSha256);

public readonly record struct CollectionResult(int DocumentsRemoved, int BlobsRemoved, int ActiveBlobs, int StagingFilesRemoved);
