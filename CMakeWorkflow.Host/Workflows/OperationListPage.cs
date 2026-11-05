namespace CMakeWorkflow.Host;

public sealed record OperationListPage(int SchemaVersion, List<OperationReference> Items, string? NextCursor);
