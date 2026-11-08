using CMakeWorkflow.History.Models;

namespace CMakeWorkflow.History.Storage;

/// <summary>Detached mutable import batch for one results directory before SQLite persistence.</summary>
/// <remarks>
/// The parser/enrichment path owns its lists and ProfileInputs map; it is not a live view of the source
/// files or a concurrent collection. Run identity derives from the directory, while SourceHash captures
/// the selected report/metadata evidence. No database writes occur by constructing this object.
/// </remarks>
internal sealed class ParsedRun
{
    public required HistoryRun Run { get; set; }
    public List<HistoryNode> Nodes { get; } = [];
    public List<HistoryMeasurement> Measurements { get; } = [];
    public List<HistoryArtifact> Artifacts { get; } = [];
    public Dictionary<string, (string? Workload, string? Scenario)> ProfileInputs { get; } = [];
}
