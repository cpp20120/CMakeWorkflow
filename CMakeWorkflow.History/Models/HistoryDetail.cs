
namespace CMakeWorkflow.History.Models;

public sealed record HistoryDetail(
    HistoryRun Run,

List<HistoryNode> Nodes,

List<HistoryMeasurement> Measurements,

List<HistoryArtifact> Artifacts);
