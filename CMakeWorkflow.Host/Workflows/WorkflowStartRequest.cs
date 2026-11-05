#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

public sealed record WorkflowStartRequest(
    string ClientRequestId,
    string Manifest,
    string ManifestSha256,
    int Jobs,
    string ResultsDirectory);
