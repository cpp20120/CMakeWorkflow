using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Workflows;

/// <summary>Read-only preflight inputs using exact bytes on the connected Host.</summary>
/// <remarks>
/// Passing preflight does not reserve trees or authorize dispatch. Explicit durable start rechecks manifest identity.
/// Canceling this read does not create or cancel a workflow operation.
/// </remarks>
/// <param name="Manifest">Absolute Host manifest path inside the initialized workspace.</param>
/// <param name="ManifestSha256">Expected SHA-256 of exact manifest bytes as 64 hexadecimal characters.</param>
/// <param name="Jobs">Proposed positive maximum node parallelism.</param>
/// <param name="ResultsDirectory">Proposed absolute Host output directory inside the workspace.</param>
public sealed record WorkflowPreflightOptions(
    string Manifest, string ManifestSha256, int Jobs, string ResultsDirectory);

/// <summary>One planner diagnostic; does not establish future runtime availability.</summary>
/// <param name="Severity">Planner severity such as Error or Warning.</param>
/// <param name="Message">Human-readable finding.</param>
public sealed record WorkflowPreflightDiagnostic(string Severity, string Message);

/// <summary>Observed prospective node and its admission diagnostics.</summary>
/// <param name="Id">Node identifier.</param>
/// <param name="Kind">Workflow node kind.</param>
/// <param name="Target">Resolved native target description.</param>
/// <param name="BuildDirectory">Resolved Host native build-tree path.</param>
/// <param name="Configuration">Resolved configuration, or null if no explicit configuration is available.</param>
/// <param name="Dependencies">Non-null expanded prerequisite IDs.</param>
/// <param name="Resources">Non-null scheduling conflict keys.</param>
/// <param name="Exclusive">Whether the node requests exclusive scheduling within this run.</param>
/// <param name="ObservedState">Current build context observation; not a live operation status.</param>
/// <param name="Diagnostics">Non-null planner findings for this node.</param>
[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record WorkflowPreflightNode(
    string Id, string Kind, string Target, string BuildDirectory, string? Configuration,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<string> Dependencies, [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<string> Resources, bool Exclusive, string ObservedState,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<WorkflowPreflightDiagnostic> Diagnostics);

/// <summary>Read-only preflight evidence suitable for explicit start request construction.</summary>
/// <remarks>
/// Valid reflects this observation only. The result is neither execution admission nor a reservation.
/// Changes in manifest bytes or Host environment may invalidate later admission.
/// </remarks>
/// <param name="SchemaVersion">Response schema version; CreateStartOptions accepts 1.</param>
/// <param name="Manifest">Host manifest path reviewed by preflight.</param>
/// <param name="ManifestSha256">SHA-256 of reviewed exact bytes.</param>
/// <param name="Jobs">Reviewed maximum workflow parallelism.</param>
/// <param name="ResultsDirectory">Reviewed Host results path.</param>
/// <param name="Valid">Whether the proposed plan has no rejecting preflight errors.</param>
/// <param name="Nodes">Non-null prospective nodes and diagnostics.</param>
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
