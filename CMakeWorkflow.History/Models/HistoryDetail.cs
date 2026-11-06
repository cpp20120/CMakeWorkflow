
// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History.Models;
#pragma warning restore IDE0130

public sealed record HistoryDetail(
    HistoryRun Run,

List<HistoryNode> Nodes,

List<HistoryMeasurement> Measurements,

List<HistoryArtifact> Artifacts);
