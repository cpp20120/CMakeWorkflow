using System.Security.Cryptography;
using Capnp.Managed;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.Core.Persistence;
using Wire = Capnp.Generated.ArtifactEvidence;

namespace CMakeWorkflow.Commands;

internal static class BinaryArtifactManifest
{
    private const int MaximumFrameBytes = 4 * 1024 * 1024;
    private static readonly MessageLimits Limits = MessageLimits.Default with
    {
        MaxMessageBytes = MaximumFrameBytes - 8,
        MaxSegmentBytes = MaximumFrameBytes - 8,
        MaxBuilderBytes = MaximumFrameBytes - 8,
        MaxSegments = 1,
        MaxListElements = 512,
        MaxTextBytes = 8192
    };

    internal static byte[] Encode(CasOperation receipt)
    {
        MessageBuilder message = new(Limits);
        Wire.ArtifactManifest.Builder root = Wire.ArtifactManifest.InitRoot(message);
        root.SetFormatVersion(checked((ushort)receipt.SchemaVersion));
        root.SetWorkspaceId(receipt.WorkspaceId);
        root.SetOperationId(receipt.OperationId);
        root.SetHostId(receipt.HostId);
        if (receipt.SchemaVersion == 1)
        {
            root.SetReportBlob(receipt.ReportDigest);
        }
        else
        {
            root.SetReportProjectionBlob(receipt.ReportDigest);
            root.SetNativeReportDocumentId(receipt.NativeReportDocumentId);
            root.SetNativeReportBlob(receipt.NativeReportBlobId);
        }
        Wire.ArtifactManifest.EntriesListBuilder entries = root.InitEntries(receipt.Artifacts.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            CasArtifact artifact = receipt.Artifacts[i];
            Wire.Artifact.Builder entry = entries[i];
            entry.SetId(artifact.ArtifactId);
            entry.SetRole(artifact.Role);
            entry.SetLogicalPath(artifact.LogicalPath);
            entry.SetBlob(artifact.Digest);
            entry.SetSize((ulong)artifact.Size);
            entry.SetMediaType(artifact.MediaType);
        }
        return CapnpWire.Frame(CapnpWire.Canonicalize(message.CopySegment()));
    }

    internal static async Task<CasOperation> ReadAsync(ArtifactStoreFiles files, CasReferenceIndex.ReceiptReference reference,
        CancellationToken ct)
    {
        string path = files.Blob(reference.Blob);
        FileInfo info = new(path);
        if (info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidDataException("Stored artifact manifest path is redirected.");
        }
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != reference.Size)
        {
            throw new InvalidDataException("Stored artifact manifest length does not match its reference.");
        }
        byte[] bytes = new byte[(int)reference.Size];
        await stream.ReadExactlyAsync(bytes, ct);
        return stream.ReadByte() == -1 && Convert.ToHexStringLower(SHA256.HashData(bytes)) == reference.Blob
            ? Decode(bytes, reference)
            : throw new InvalidDataException("Stored artifact manifest bytes do not match their reference.");
    }

    private static CasOperation Decode(ReadOnlySpan<byte> frame, CasReferenceIndex.ReceiptReference reference)
    {
        Wire.ArtifactManifest.Reader root = Wire.ArtifactManifest.ReadRoot(FrameCodec.Read(frame, Limits));
        string? workspace = root.DecodeWorkspaceId();
        string? report = root.FormatVersion == 1 ? root.DecodeReportBlob() : root.DecodeReportProjectionBlob();
        if (root.FormatVersion is not (1 or 2) || root.DecodeOperationId() != reference.OperationId ||
            string.IsNullOrWhiteSpace(workspace) || !ArtifactStoreFiles.IsCanonicalHex(report, 64) ||
            root.Entries.Count is < 1 or > 512)
        {
            throw new InvalidDataException("Unsupported or invalid artifact manifest.");
        }

        List<CasArtifact> entries = [];
        string? previous = null;
        foreach (Wire.Artifact.Reader entry in root.Entries)
        {
            string? id = entry.DecodeId();
            string? role = entry.DecodeRole();
            string? logicalPath = entry.DecodeLogicalPath();
            string? blob = entry.DecodeBlob();
            string? type = entry.DecodeMediaType();
            if (string.IsNullOrWhiteSpace(id) || (previous is not null && string.CompareOrdinal(previous, id) >= 0) ||
                string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(logicalPath) || string.IsNullOrWhiteSpace(type) ||
                !ArtifactStoreFiles.IsCanonicalHex(blob, 64) || entry.Size > long.MaxValue)
            {
                throw new InvalidDataException("Invalid or unordered artifact manifest entry.");
            }
            previous = id;
            entries.Add(new(id, role, logicalPath, blob!, (long)entry.Size, type));
        }

        CasOperation decoded = new(root.FormatVersion, workspace, reference.OperationId, root.DecodeHostId(), report!, reference.Pinned, entries)
        {
            ArtifactManifestBlobId = reference.Blob,
            NativeReportDocumentId = root.DecodeNativeReportDocumentId(),
            NativeReportBlobId = root.DecodeNativeReportBlob()
        };
        bool native = root.FormatVersion == 1
            ? !root.HasNativeReportDocumentId && !root.HasNativeReportBlob && !root.HasReportProjectionBlob
            : !root.HasReportBlob && ArtifactStoreFiles.IsCanonicalHex(decoded.NativeReportDocumentId, 64) &&
              ArtifactStoreFiles.IsCanonicalHex(decoded.NativeReportBlobId, 64) &&
              entries.SingleOrDefault(entry => entry.ArtifactId == "native-report")?.Digest == decoded.NativeReportBlobId;
        return native && entries.SingleOrDefault(entry => entry.ArtifactId == "report")?.Digest == report &&
            frame.SequenceEqual(Encode(decoded))
            ? decoded : throw new InvalidDataException("Artifact manifest is noncanonical or missing report evidence.");
    }
}
