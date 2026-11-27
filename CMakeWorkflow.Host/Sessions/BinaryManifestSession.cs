using System.Security.Cryptography;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Storage.Documents;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    /// <summary>Stages a canonical workflow frame. An empty payload probes for previously verified exact bytes.</summary>
    public Task<WorkflowManifestPreparationReceipt?> PrepareBinaryManifestAsync(WorkflowManifestPreparation request,
        CancellationToken ct) => ApplicationCallAsync(request, async (paths, token) =>
    {
        if (request.ManifestBytes.Length > WorkflowManifestPreparation.MaximumBytes ||
            request.Sha256 is not { Length: 64 } || !request.Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Expected a workflow frame of at most 1 MiB and its SHA-256 digest.");
        }

        string workspace = paths.WorkspaceDirectory;
        string directory = WorkspaceRelativePath(workspace, request.ManifestBase);
        if (!WorkflowPlanner.PathComparer.Equals(paths.Require(directory), directory))
        {
            throw new ApplicationRefusal("path_not_allowed", "Manifest base must not traverse a redirected directory.");
        }

        string results = paths.Require(Path.IsPathFullyQualified(request.ResultsDirectory) ? request.ResultsDirectory
            : WorkspaceRelativePath(workspace, request.ResultsDirectory));
        string digest = new BlobId(request.Sha256).Value;
        string name = ".cmwf-workflow-" + digest + ".capnp";
        string destination = Path.Combine(directory, name);
        if (WorkflowLedgerPath.InBinaryMetadata(workspace, destination) || WorkflowLedgerPath.InBinaryMetadata(workspace, results))
        {
            throw new ApplicationRefusal("path_not_allowed", "Submission and results paths must be outside execution metadata.");
        }

        if (request.ManifestBytes.IsEmpty)
        {
            await using FileStream? existing = OperationArtifactFile.OpenCatalogFile(workspace, destination, optional: true);
            if (existing is null)
            {
                return null;
            }

            if (existing.Length is < 16 or > WorkflowManifestPreparation.MaximumBytes)
            {
                throw new InvalidDataException("Staged binary workflow has an invalid length.");
            }

            byte[] contents = new byte[(int)existing.Length];
            await existing.ReadExactlyAsync(contents, token);
            if (existing.ReadByte() != -1)
            {
                throw new InvalidDataException("Staged binary workflow changed during verification.");
            }

            _ = VerifyFrame(contents, digest);
        }
        else
        {
            _ = VerifyFrame(request.ManifestBytes.Span, digest);
            await OperationArtifactFile.PublishManifestAsync(workspace, directory, name, request.ManifestBytes, digest, token);
        }

        return new WorkflowManifestPreparationReceipt(1, destination, digest, results);
    }, ct);

    private static WorkflowDocument VerifyFrame(ReadOnlySpan<byte> frame, string digest)
    {
        if (!Convert.ToHexString(SHA256.HashData(frame)).Equals(digest, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApplicationRefusal("manifest_changed", "Workflow frame differs from the declared SHA-256.");
        }

        ReadOnlySpan<byte> segment = CapnpWire.Unframe(frame);
        return segment.SequenceEqual(CapnpWire.Canonicalize(segment))
            ? ProductionWorkflowCodec.Decode(segment)
            : throw new InvalidDataException("Expected a canonical workflow frame.");
    }
}
