
// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

public sealed record OperationListPage(int SchemaVersion,
List<OperationReference> Items, string? NextCursor);
