using System.Text.Json.Serialization;
using CMakeWorkflow;
using CMakeWorkflow.History;
using CMakeWorkflow.Profiling;
using StreamJsonRpc;

namespace CMakeWorkflow.Host;

public sealed record HistoryDetectRequest(
    string DatabasePath,
    string BaselineRunId,
    string CandidateRunId,
    string Metric,
    string Direction,
    double MinimumEffectPercent,
    string? Variant = null,
    string? Scenario = null,
    double Alpha = 0.05);
