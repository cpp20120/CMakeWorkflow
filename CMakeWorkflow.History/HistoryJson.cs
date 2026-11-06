using System.Text.Json.Serialization;
using CMakeWorkflow.History.Analysis;
using CMakeWorkflow.History.Models;

namespace CMakeWorkflow.History;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<HistoryRun>))]
[JsonSerializable(typeof(HistoryDetail))]
[JsonSerializable(typeof(List<HistoryMeasurement>))]
[JsonSerializable(typeof(List<HistoryDifference>))]
[JsonSerializable(typeof(HistoryHostEnvironment))]
[JsonSerializable(typeof(List<HistoryBuildEnvironment>))]
[JsonSerializable(typeof(HistoryImportResult))]
[JsonSerializable(typeof(HistoryDetectionReport))]
internal sealed partial class HistoryJson : JsonSerializerContext;
