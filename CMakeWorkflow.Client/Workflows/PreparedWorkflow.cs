#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

// Public .NET SDK DTOs outside the shared recipe/model contracts.
// TypeScript uses the same JSON field names.
public sealed record PreparedWorkflow(
    int SchemaVersion,
    string Manifest,
    string ManifestSha256,
    string ResultsDirectory);
