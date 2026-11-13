using CMakeWorkflow.Contracts.History;

namespace CMakeWorkflow.Host.Workflows;

// A read-only observation of the exact manifest bytes supplied to planning.
// This is not an admission token: workflow/start must still validate and durably
// bind the manifest SHA-256. A file can change after preflight.
public sealed record WorkflowPreflightRequest(
    string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory)
{
    public IReadOnlyList<string>? Only { get; init; }

    public HistoryPolicy? HistoryPolicy { get; init; }
}

public sealed record WorkflowPreflightDiagnostic(string Severity, string Message);

public sealed record WorkflowPreflightNode(
    string Id, string Kind, string Target, string BuildDirectory, string? Configuration,

List<string> Dependencies,
List<string> Resources, bool Exclusive, string ObservedState,

List<WorkflowPreflightDiagnostic> Diagnostics);

public sealed record WorkflowPreflightResult(
    int SchemaVersion, string Manifest, string ManifestSha256, int Jobs,
    string ResultsDirectory, bool Valid,
List<WorkflowPreflightNode> Nodes)
{
    public IReadOnlyList<string>? Only { get; init; }

    public HistoryPolicy? HistoryPolicy { get; init; }
}
