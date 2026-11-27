using CMakeWorkflow.Contracts.History;

namespace CMakeWorkflow.Host.Workflows;

// A domain-qualified durable start. Existing workflow/start remains wire-compatible.
public sealed record ExecutionStartRequest(string Domain, string ClientRequestId,
    string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory)
{
    public string? SourceSnapshotId { get; init; }
    public IReadOnlyList<string>? Only { get; init; }
    public HistoryPolicy? HistoryPolicy { get; init; }
}
