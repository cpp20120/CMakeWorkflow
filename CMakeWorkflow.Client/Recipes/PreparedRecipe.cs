namespace CMakeWorkflow.Client.Recipes;

public sealed record PreparedRecipe(int SchemaVersion, string Manifest, string ManifestSha256);
