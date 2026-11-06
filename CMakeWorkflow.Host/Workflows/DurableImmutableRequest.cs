// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

// The immutable request identity is derived by a trusted domain adapter.
// The SQLite v1 column names remain unchanged for on-disk compatibility.
/// <summary>Trusted domain adapter input for immutable workspace acceptance.</summary>
/// <remarks>
/// RequestDigest must encode all execution-relevant request parameters in a stable domain-specific form.
/// The ledger compares this digest before reading current snapshot bytes on matching retries. Construction itself
/// neither snapshots the file, reserves results nor authorizes dispatch.
/// </remarks>
/// <param name="ClientRequestId">Workspace-scoped caller request identity.</param>
/// <param name="SnapshotPath">Absolute authorized Host path from which new acceptance reads exact bytes.</param>
/// <param name="SnapshotSha256">Expected SHA-256 of those bytes.</param>
/// <param name="ResultsDirectory">Absolute authorized results path; ownership is reserved separately before authorization.</param>
/// <param name="RequestDigest">SHA-256 of canonical request identity, including domain semantics.</param>
/// <param name="DomainKind">Persisted execution domain; legacy CMake acceptance uses workflow.</param>
internal sealed record DurableImmutableRequest(
    string ClientRequestId,
    string SnapshotPath,
    string SnapshotSha256,
    string ResultsDirectory,
    string RequestDigest,
    string DomainKind = "workflow");
