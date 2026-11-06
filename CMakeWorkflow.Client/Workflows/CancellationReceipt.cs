namespace CMakeWorkflow.Client.Workflows;

/// <summary>Result of a durable cancellation-intent mutation.</summary>
/// <remarks>
/// Requested=true is not proof of stopped workers or a terminal canceled outcome.
/// Revision belongs to cancellation intent and is independent of recovery reconciliation revisions.
/// </remarks>
/// <param name="OperationId">Target operation ID.</param>
/// <param name="State">cancel_requested, or the already terminal state.</param>
/// <param name="Revision">Cancellation-intent revision: 0 before intent, 1 afterward.</param>
/// <param name="Requested">Whether intent is recorded for a nonterminal operation; false if already terminal.</param>
public sealed record CancellationReceipt(string OperationId, string State, long Revision, bool Requested);
