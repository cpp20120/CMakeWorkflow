// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

public sealed record OperationReference(
    string WorkspaceId,
    string OperationId,
    string ClientRequestId,
    string Kind,
    string State,
    string ManifestSha256,
    string ManifestBaseDirectory);
