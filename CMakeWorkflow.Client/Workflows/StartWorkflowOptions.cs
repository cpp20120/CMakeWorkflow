namespace CMakeWorkflow.Client.Workflows;

/// <summary>Immutable durable workflow admission inputs interpreted on the connected Host.</summary>
/// <remarks>
/// Identical normalized request identities under one ID return the same acceptance; conflicting reuse fails.
/// Cancellation or a missing reply does not establish rejection. Persist the ID and find it in the same workspace.
/// </remarks>
/// <param name="ClientRequestId">Caller-chosen nonempty ID, at most 128 characters; persist before submission.</param>
/// <param name="Manifest">Absolute Host manifest path within the workspace; sent unchanged over SSH.</param>
/// <param name="ManifestSha256">Expected SHA-256 of exact remote manifest bytes, as 64 hexadecimal characters.</param>
/// <param name="Jobs">Maximum concurrently admitted workflow nodes; at least 1.</param>
/// <param name="ResultsDirectory">Absolute Host output path in the workspace; reserved durably before dispatch authorization.</param>
public sealed record StartWorkflowOptions(
    string ClientRequestId,
    string Manifest,
    string ManifestSha256,
    int Jobs,
    string ResultsDirectory);
