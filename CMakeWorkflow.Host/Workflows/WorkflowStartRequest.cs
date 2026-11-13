using CMakeWorkflow.Contracts.History;

namespace CMakeWorkflow.Host.Workflows;

public sealed record WorkflowStartRequest(
    string ClientRequestId,
    string Manifest,
    string ManifestSha256,
    int Jobs,
    string ResultsDirectory)
{
    public IReadOnlyList<string>? Only { get; init; }

    public HistoryPolicy? HistoryPolicy { get; init; }
}
