#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History;
#pragma warning restore IDE0130

internal sealed class ParsedRun
{
    public required HistoryRun Run { get; set; }
    public List<HistoryNode> Nodes { get; } = [];
    public List<HistoryMeasurement> Measurements { get; } = [];
    public List<HistoryArtifact> Artifacts { get; } = [];
    public Dictionary<string, (string? Workload, string? Scenario)> ProfileInputs { get; } = [];
}
