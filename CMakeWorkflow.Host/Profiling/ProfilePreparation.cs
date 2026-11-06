// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Profiling;
#pragma warning restore IDE0130

public sealed record ProfilePreparation(
    int SchemaVersion,
    string Manifest,
    string ManifestSha256,
    string ResultsDirectory);
