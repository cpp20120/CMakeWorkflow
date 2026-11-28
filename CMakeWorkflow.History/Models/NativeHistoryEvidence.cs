namespace CMakeWorkflow.History.Models;

/// <summary>Identity of the verified terminal document from which a run was indexed.</summary>
public sealed record NativeHistoryEvidence(string OperationId, string DocumentId, string BlobId, int SchemaVersion);
