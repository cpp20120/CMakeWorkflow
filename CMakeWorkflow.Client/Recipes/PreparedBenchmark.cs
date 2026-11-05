namespace CMakeWorkflow.Client.Recipes;

public sealed record PreparedBenchmark(int SchemaVersion, string Manifest, string ManifestSha256);
