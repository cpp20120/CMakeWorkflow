using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
internal partial class HistoryJson : JsonSerializerContext;
