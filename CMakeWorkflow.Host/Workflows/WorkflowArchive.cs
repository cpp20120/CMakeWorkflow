using CMakeWorkflow.Storage.Documents;

namespace CMakeWorkflow.Host.Workflows;

/// <summary>Copies an accepted workflow into a durable, format-aware archive.</summary>
public static class WorkflowArchive
{
    public static async Task<MigrationResult> ArchiveAcceptedAsync(string workspace, string operationId,
        string artifactStore, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(operationId);
        if (operationId.Length != 32 || !operationId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid operation ID.", nameof(operationId));
        }

        byte[] snapshot;
        AcceptedWorkflow accepted;
        using (DurableAcceptanceStore ledger = new(workspace, readOnly: true))
        {
            accepted = ledger.Get(operationId) ?? throw new KeyNotFoundException("Unknown operation.");
            if (ledger.GetDomainKind(operationId) != "workflow")
            {
                throw new InvalidOperationException("Only workflow operations have workflow manifests.");
            }

            snapshot = ledger.ReadSnapshot(operationId, DocumentStore.MaximumStoredDocumentBytes);
        }

        DocumentStore documents = await DocumentStore.OpenProductionStoreAsync(artifactStore, ct);
        return await documents.ArchiveWorkflowAsync(snapshot, new LegacyDigest(accepted.ManifestSha256),
            $"workflow/{accepted.WorkspaceId}/{accepted.OperationId}", ct);
    }
}
