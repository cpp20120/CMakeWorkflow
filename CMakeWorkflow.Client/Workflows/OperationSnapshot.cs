namespace CMakeWorkflow.Client.Workflows;

/// <summary>Durable operation state projection, not a live worker snapshot.</summary>
/// <remarks>
/// Terminal states are succeeded, failed and canceled. cancel_requested records intent only.
/// accepted_not_dispatched and dispatch_authorized describe admission; authorized does not prove a process started.
/// </remarks>
/// <param name="WorkspaceId">Durable workspace identity.</param>
/// <param name="OperationId">Durable operation identifier.</param>
/// <param name="ClientRequestId">Caller request ID used for lost-reply lookup.</param>
/// <param name="Kind">Execution domain; legacy CMake operations use workflow.</param>
/// <param name="State">Durable admission, cancellation-intent or terminal state.</param>
/// <param name="ManifestSha256">SHA-256 of immutable accepted manifest bytes.</param>
/// <param name="ManifestBaseDirectory">Host directory used to resolve relative manifest paths.</param>
public sealed record OperationSnapshot(
    string WorkspaceId,
    string OperationId,
    string ClientRequestId,
    string Kind,
    string State,
    string ManifestSha256,
    string ManifestBaseDirectory);
