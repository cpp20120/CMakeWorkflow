using System.Diagnostics.CodeAnalysis;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

// A read-only observation of the exact manifest bytes supplied to planning.
// This is not an admission token: workflow/start must still validate and durably
// bind the manifest SHA-256. A file can change after preflight.
public sealed record WorkflowPreflightRequest(
    string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);

public sealed record WorkflowPreflightDiagnostic(string Severity, string Message);

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record WorkflowPreflightNode(
    string Id, string Kind, string Target, string BuildDirectory, string? Configuration,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<string> Dependencies, [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<string> Resources, bool Exclusive, string ObservedState,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<WorkflowPreflightDiagnostic> Diagnostics);

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record WorkflowPreflightResult(
    int SchemaVersion, string Manifest, string ManifestSha256, int Jobs,
    string ResultsDirectory, bool Valid, [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<WorkflowPreflightNode> Nodes);
