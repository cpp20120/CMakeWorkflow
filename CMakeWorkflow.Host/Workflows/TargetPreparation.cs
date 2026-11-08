namespace CMakeWorkflow.Host.Workflows;

public sealed record TargetPreparation(
    int SchemaVersion,
    string Manifest,
    string ManifestSha256,
    string ResultsDirectory);
