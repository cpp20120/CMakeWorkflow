#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

/// <summary>Durable admission inputs, interpreted by the connected Host.</summary>
/// <param name="ClientRequestId">Persist this caller-chosen ID before submission; use operations/find after a lost response.</param>
/// <param name="Manifest">Absolute Host filesystem path to an existing manifest in the initialized workspace.</param>
/// <param name="ManifestSha256">SHA-256 of the expected remote manifest bytes, obtained before submission.</param>
/// <param name="Jobs">Maximum workflow parallelism.</param>
/// <param name="ResultsDirectory">Absolute Host filesystem output directory within the initialized workspace.</param>
public sealed record StartWorkflowOptions(
    string ClientRequestId,
    string Manifest,
    string ManifestSha256,
    int Jobs,
    string ResultsDirectory);
