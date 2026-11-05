namespace CMakeWorkflow.Client;

/// <summary>Filter workspace operations with bounded keyset pagination.</summary>
/// <remarks>New admissions are excluded from subsequent pages, but state changes are not snapshot-isolated.</remarks>
public sealed record OperationQuery(int Limit = 50, string? State = null,
    string? Domain = null, string? Cursor = null);

public sealed record OperationPage(int SchemaVersion, List<OperationSnapshot> Items, string? NextCursor);
