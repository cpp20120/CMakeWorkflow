namespace CMakeWorkflow.Client.Workflows;

// Public .NET SDK DTOs outside the shared recipe/model contracts.
// TypeScript uses the same JSON field names.
/// <summary>Host-side manifest preparation result; no operation has been started.</summary>
/// <param name="SchemaVersion">Preparation response schema version.</param>
/// <param name="Manifest">Absolute Host manifest path.</param>
/// <param name="ManifestSha256">SHA-256 of exact prepared bytes for preflight/admission.</param>
/// <param name="ResultsDirectory">Proposed Host results path; preparation alone does not reserve durable ownership.</param>
public sealed record PreparedWorkflow(
    int SchemaVersion,
    string Manifest,
    string ManifestSha256,
    string ResultsDirectory);
