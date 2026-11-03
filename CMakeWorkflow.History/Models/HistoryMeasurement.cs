using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.History;

public sealed record HistoryMeasurement(
    string RunId,
    string? NodeId,
    string Metric,
    string Unit,
    double Value,
    string? Variant,
    string? Scenario,
    string Source,
    string Method,
    int SampleIndex,
    string? Workload,
    string? Environment);
