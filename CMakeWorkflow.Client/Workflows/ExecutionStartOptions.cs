namespace CMakeWorkflow.Client.Workflows;

// Paths and hashes are for the Host filesystem, not the local Client machine.
/// <summary>Domain-qualified durable admission parameters, interpreted on the Host.</summary>
/// <remarks>
/// Paths and hashes refer to the Host filesystem; the SDK sends them unchanged.
/// Save ClientRequestId before submission. Acceptance is not proof of dispatch or completion.
/// </remarks>
/// <param name="Domain">Host-supported execution domain, such as process.</param>
/// <param name="ClientRequestId">Caller identity, 1..128 non-whitespace characters; conflicting reuse is rejected.</param>
/// <param name="Manifest">Absolute Host manifest path within the initialized workspace.</param>
/// <param name="ManifestSha256">Expected SHA-256 of exact manifest bytes, as 64 hexadecimal characters.</param>
/// <param name="Jobs">Maximum concurrently admitted workflow nodes; at least 1.</param>
/// <param name="ResultsDirectory">Absolute Host results directory in the workspace; durable ownership is retained across interruption.</param>
public sealed record ExecutionStartOptions(string Domain, string ClientRequestId,
    string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);
