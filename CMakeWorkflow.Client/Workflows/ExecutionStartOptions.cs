namespace CMakeWorkflow.Client.Workflows;

// Paths and hashes are for the Host filesystem, not the local Client machine.
public sealed record ExecutionStartOptions(string Domain, string ClientRequestId,
    string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);
