#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

// The immutable request identity is derived by a trusted domain adapter.
// The SQLite v1 column names remain unchanged for on-disk compatibility.
internal sealed record DurableImmutableRequest(
    string ClientRequestId,
    string SnapshotPath,
    string SnapshotSha256,
    string ResultsDirectory,
    string RequestDigest,
    string DomainKind = "workflow");
