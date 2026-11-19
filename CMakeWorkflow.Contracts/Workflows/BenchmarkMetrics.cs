using System.Text.Json.Serialization;

namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>Extracts numeric measurements from one JSON object on standard output.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BenchmarkMetrics(
    [property: JsonRequired] string Source,
    [property: JsonRequired] string Format,
    [property: JsonRequired] IReadOnlyList<BenchmarkMetricField> Fields);

/// <summary>A dot-separated property path, History metric name, unit and improvement direction.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BenchmarkMetricField(
    [property: JsonRequired] string Path,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Unit,
    [property: JsonRequired] string Direction);
