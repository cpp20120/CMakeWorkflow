// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

public sealed record TargetPreparation(
    int SchemaVersion,
    string Manifest,
    string ManifestSha256,
    string ResultsDirectory);
