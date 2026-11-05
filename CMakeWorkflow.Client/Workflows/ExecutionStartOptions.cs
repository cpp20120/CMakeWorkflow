#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

// Paths and hashes are for the Host filesystem, not the local Client machine.
public sealed record ExecutionStartOptions(string Domain, string ClientRequestId,
    string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);
