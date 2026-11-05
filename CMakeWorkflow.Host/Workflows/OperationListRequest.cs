namespace CMakeWorkflow.Host;

/// <summary>Query durable operations in this initialized workspace, newest first.</summary>
/// <remarks>Cursor pagination excludes later admissions; mutable operation states may change between pages.</remarks>
public sealed record OperationListRequest(int Limit = 50, string? State = null,
    string? Domain = null, string? Cursor = null);
