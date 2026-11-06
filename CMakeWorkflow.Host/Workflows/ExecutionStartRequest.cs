// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

// A domain-qualified durable start. Existing workflow/start remains wire-compatible.
public sealed record ExecutionStartRequest(string Domain, string ClientRequestId,
    string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);
