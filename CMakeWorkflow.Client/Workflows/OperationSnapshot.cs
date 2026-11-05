#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

public sealed record OperationSnapshot(
    string WorkspaceId,
    string OperationId,
    string ClientRequestId,
    string Kind,
    string State,
    string ManifestSha256,
    string ManifestBaseDirectory);
