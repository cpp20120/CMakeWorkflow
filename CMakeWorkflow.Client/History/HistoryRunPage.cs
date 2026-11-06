using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.History;

[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.History.HistoryRunPage(Items) participates in CMakeWorkflow.Client.History.HistoryService.RunsPageAsync; retain these concrete collection parameter types.")]
public sealed record HistoryRunPage(int SchemaVersion, [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.History.HistoryRunPage.Items (List<HistoryRun>) is exposed through CMakeWorkflow.Client.History.HistoryService.RunsPageAsync; retain this Client SDK property signature.")]
List<HistoryRun> Items, string? NextCursor);
