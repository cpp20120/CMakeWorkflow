namespace CMakeWorkflow.Client.Workflows;

public sealed record OperationSnapshot(
    string WorkspaceId,
    string OperationId,
    string ClientRequestId,
    string Kind,
    string State,
    string ManifestSha256,
    string ManifestBaseDirectory);
