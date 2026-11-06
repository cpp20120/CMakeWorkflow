// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Recipes;
#pragma warning restore IDE0130

public sealed record BenchmarkMatrixPreparation(int SchemaVersion, string Manifest, string ManifestSha256);
