namespace CMakeWorkflow.Host.Workflows;

// A domain-qualified durable start. Existing workflow/start remains wire-compatible.
public sealed record ExecutionStartRequest(string Domain, string ClientRequestId,
    string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);
