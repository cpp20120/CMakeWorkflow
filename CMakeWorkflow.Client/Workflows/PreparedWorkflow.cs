namespace CMakeWorkflow.Client.Workflows;

// Public .NET SDK DTOs outside the shared recipe/model contracts.
// TypeScript uses the same JSON field names.
public sealed record PreparedWorkflow(
    int SchemaVersion,
    string Manifest,
    string ManifestSha256,
    string ResultsDirectory);
