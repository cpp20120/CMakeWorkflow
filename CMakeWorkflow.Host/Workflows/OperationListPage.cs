
namespace CMakeWorkflow.Host.Workflows;

public sealed record OperationListPage(int SchemaVersion,
List<OperationReference> Items, string? NextCursor);
