using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Workflows;

/// <summary>Read-only preflight over manifest bytes on the connected Host.</summary>
/// <remarks>
/// Passing preflight is not authorization to dispatch: the user must explicitly
/// call Workflows.StartAsync. A changed manifest is rejected at durable admission.
/// </remarks>
public sealed record WorkflowPreflightOptions(
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
List<WorkflowPreflightNode> Nodes)
{
    /// <summary>Construct an explicit start request from reviewed Host values; does not dispatch.</summary>
    public StartWorkflowOptions CreateStartOptions(string clientRequestId)
    {
        return SchemaVersion != 1 || !Valid
            ? throw new InvalidOperationException("Cannot start an invalid or unsupported preflight result.")
            : string.IsNullOrWhiteSpace(clientRequestId)
            ? throw new ArgumentException("A durable client request ID is required.", nameof(clientRequestId))
            : new(clientRequestId, Manifest, ManifestSha256, Jobs, ResultsDirectory);
    }
}
