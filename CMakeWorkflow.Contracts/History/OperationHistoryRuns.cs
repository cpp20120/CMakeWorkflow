namespace CMakeWorkflow.Contracts.History;

/// <summary>An indexed run and the source identity recorded when its import committed.</summary>
public sealed record IndexedRunIdentity(string RunId, string SourceHash);

/// <summary>Run identities bound to an indexed durable operation in the initialized Host workspace.</summary>
public sealed record OperationHistoryRuns(int SchemaVersion, string OperationId, string DatabasePath,
    IReadOnlyList<IndexedRunIdentity> Runs);
