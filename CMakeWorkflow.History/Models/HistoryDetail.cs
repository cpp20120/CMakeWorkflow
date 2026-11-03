using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.History;

public sealed record HistoryDetail(
    HistoryRun Run,
    List<HistoryNode> Nodes,
    List<HistoryMeasurement> Measurements,
    List<HistoryArtifact> Artifacts);
