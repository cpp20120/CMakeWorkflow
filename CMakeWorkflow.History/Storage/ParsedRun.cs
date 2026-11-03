using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.History;

internal sealed class ParsedRun
{
    public required HistoryRun Run { get; set; }
    public List<HistoryNode> Nodes { get; } = [];
    public List<HistoryMeasurement> Measurements { get; } = [];
    public List<HistoryArtifact> Artifacts { get; } = [];
    public Dictionary<string, (string? Workload, string? Scenario)> ProfileInputs { get; } = [];
}
