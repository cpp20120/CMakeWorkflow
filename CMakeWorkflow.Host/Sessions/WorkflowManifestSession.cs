using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Workflows;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    public Task<WorkflowManifestPreparationReceipt> PrepareManifestAsync(WorkflowManifestPreparation request,
        CancellationToken ct) => ApplicationCallAsync(request, async (paths, token) =>
    {
        if (request.ManifestBytes is not { Length: > 0 and <= WorkflowManifestPreparation.MaximumBytes } ||
            request.Sha256 is not { Length: 64 } || !request.Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Expected up to 1 MiB of manifest bytes and their SHA-256 digest.");
        }
        string digest = Convert.ToHexStringLower(SHA256.HashData(request.ManifestBytes.Span));
        if (!digest.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApplicationRefusal("manifest_changed", "Manifest bytes differ from the declared SHA-256.");
        }
        WorkflowDocument? manifest = JsonSerializer.Deserialize(request.ManifestBytes.Span, WorkflowDefinitionJson.Default.WorkflowDocument);
        if (manifest is not { SchemaVersion: 1 or 2, Contexts: not null, Nodes.Count: > 0 })
        {
            throw new InvalidDataException("Expected a standard workflow manifest.");
        }

        string workspace = paths.WorkspaceDirectory;
        string basePath = WorkspaceRelativePath(workspace, request.ManifestBase);
        if (!WorkflowPlanner.PathComparer.Equals(paths.Require(basePath), basePath))
        {
            throw new ApplicationRefusal("path_not_allowed", "Manifest base must not traverse a redirected directory.");
        }
        string output = string.IsNullOrWhiteSpace(request.ResultsDirectory)
            ? throw new ArgumentException("A Host results directory is required.")
            : paths.Require(Path.IsPathFullyQualified(request.ResultsDirectory) ? request.ResultsDirectory
                : WorkspaceRelativePath(workspace, request.ResultsDirectory));
        string fileName = ".cmwf-workflow-" + digest + ".json";
        string destination = Path.Combine(basePath, fileName);
        await OperationArtifactFile.PublishManifestAsync(workspace, basePath, fileName, request.ManifestBytes, digest, token);
        return new WorkflowManifestPreparationReceipt(1, destination, digest, output);
    }, ct);

    private static string WorkspaceRelativePath(string workspace, string relative)
    {
        bool invalid = relative != "." && (string.IsNullOrWhiteSpace(relative) || relative.Length > 4096 ||
            relative.Split('/').Any(part => part is "" or "." or ".." || part.Length > 255 ||
                part.IndexOfAny(['\\', ':', '\0']) >= 0 || part.TrimEnd(' ', '.') != part));
        return invalid
            ? throw new ApplicationRefusal("path_not_allowed", "Expected a workspace-relative path without traversal.")
            : Path.GetFullPath(Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar)));
    }
}
