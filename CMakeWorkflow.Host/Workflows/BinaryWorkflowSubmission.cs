using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Storage.Documents;

namespace CMakeWorkflow.Host.Workflows;

/// <summary>Initializes and submits versioned native workflow execution inputs.</summary>
public static class BinaryWorkflowSubmission
{
    public const string Domain = "cmake-capnp-v1";

    /// <summary>Fences a fresh operation ledger namespace before enabling binary admission.</summary>
    public static void InitializeWorkspace(string workspace)
    {
        if (!Path.IsPathFullyQualified(workspace) || !Directory.Exists(workspace))
        {
            throw new ArgumentException("An existing absolute workspace directory is required.", nameof(workspace));
        }

        string canonical = new WorkspaceReadPaths(workspace).WorkspaceDirectory;
        WorkflowLedgerPath.Fence(canonical);
        using DurableAcceptanceStore ledger = new(canonical);
    }

    /// <summary>Submits an already published frame through the same coordinator used by the Host RPC.</summary>
    public static Task<OperationReference> StartAsync(DurableExecutionCoordinator coordinator, string workspace,
        ExecutionStartRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(request);
        return request.Domain == Domain
            ? coordinator.StartBinaryAsync(workspace, request, ct)
            : throw new ArgumentException("Unsupported binary workflow submission domain.", nameof(request));
    }

    /// <summary>Opens the exact verified binary input of an accepted operation.</summary>
    public static async Task<DocumentSession> OpenAcceptedDocumentAsync(string workspace, string operationId,
        CancellationToken ct = default)
    {
        WorkspaceReadPaths paths = new(workspace);
        using DurableAcceptanceStore ledger = new(paths.WorkspaceDirectory, readOnly: true);
        BinaryWorkflowInput input = ledger.ReadBinaryInput(operationId)
            ?? throw new InvalidOperationException("Operation has no native binary input.");
        return await OpenVerifiedAsync(ledger, input.Document, ct);
    }

    internal static async Task<DocumentSession> OpenVerifiedAsync(DurableAcceptanceStore ledger, DocumentDescriptor expected,
        CancellationToken ct)
    {
        DocumentStore documents = await ledger.OpenExecutionDocumentsAsync(ct);
        DocumentSession session = await documents.OpenAsync(expected.Id, ct: ct);
        if (session.Descriptor != expected || !session.Verification.CanonicalSegment)
        {
            session.Dispose();
            throw new InvalidDataException("Binary workflow document differs from admission.");
        }

        return session;
    }

}
