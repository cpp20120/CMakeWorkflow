namespace CMakeWorkflow.Host.Recipes;

public sealed record BuildMatrixPreparation(int SchemaVersion, string Manifest, string ManifestSha256);
