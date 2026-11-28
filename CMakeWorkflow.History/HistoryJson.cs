using System.Text.Json.Serialization;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.History.Analysis;
using CMakeWorkflow.History.Models;

namespace CMakeWorkflow.History;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<HistoryRun>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(HistoryDetail))]
[JsonSerializable(typeof(List<HistoryMeasurement>))]
[JsonSerializable(typeof(BenchmarkTrial))]
[JsonSerializable(typeof(List<HistoryDifference>))]
[JsonSerializable(typeof(HistoryHostEnvironment))]
[JsonSerializable(typeof(List<HistoryBuildEnvironment>))]
[JsonSerializable(typeof(HistoryImportResult))]
[JsonSerializable(typeof(HistoryDetectionReport))]
[JsonSerializable(typeof(HistoryHostRow[]))]
internal sealed partial class HistoryJson : JsonSerializerContext;
