namespace CMakeWorkflow.Client.Recipes;

/// <summary>Prepared Host manifest evidence; does not imply admission or execution.</summary>
/// <param name="SchemaVersion">Preparation response schema version.</param>
/// <param name="Manifest">Absolute Host manifest path.</param>
/// <param name="ManifestSha256">SHA-256 of exact manifest bytes for preflight and durable start.</param>
public sealed record PreparedRecipe(int SchemaVersion, string Manifest, string ManifestSha256);
