namespace CMakeWorkflow.Host.Profiling;

public sealed record ProfilePreparation(
    int SchemaVersion,
    string Manifest,
    string ManifestSha256,
    string ResultsDirectory);
