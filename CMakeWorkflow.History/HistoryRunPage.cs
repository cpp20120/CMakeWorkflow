using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.History;

/// <summary>Workspace-authorized, keyset-paginated run history.</summary>
[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record HistoryRunPage(int SchemaVersion, [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<HistoryRun> Items, string? NextCursor);
