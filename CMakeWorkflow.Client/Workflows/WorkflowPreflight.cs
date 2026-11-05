namespace CMakeWorkflow.Client;

/// <summary>Read-only preflight over manifest bytes on the connected Host.</summary>
/// <remarks>
/// Passing preflight is not authorization to dispatch: the user must explicitly
/// call Workflows.StartAsync. A changed manifest is rejected at durable admission.
/// </remarks>
public sealed record WorkflowPreflightOptions(
    string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);

public sealed record WorkflowPreflightDiagnostic(string Severity, string Message);

public sealed record WorkflowPreflightNode(
    string Id, string Kind, string Target, string BuildDirectory, string? Configuration,
    List<string> Dependencies, List<string> Resources, bool Exclusive, string ObservedState,
    List<WorkflowPreflightDiagnostic> Diagnostics);

public sealed record WorkflowPreflightResult(
    int SchemaVersion, string Manifest, string ManifestSha256, int Jobs,
    string ResultsDirectory, bool Valid, List<WorkflowPreflightNode> Nodes)
{
    /// <summary>Construct an explicit start request from reviewed Host values; does not dispatch.</summary>
    public StartWorkflowOptions CreateStartOptions(string clientRequestId)
    {
        if (SchemaVersion != 1 || !Valid)
            throw new InvalidOperationException("Cannot start an invalid or unsupported preflight result.");
        if (string.IsNullOrWhiteSpace(clientRequestId))
            throw new ArgumentException("A durable client request ID is required.", nameof(clientRequestId));
        return new(clientRequestId, Manifest, ManifestSha256, Jobs, ResultsDirectory);
    }
}
