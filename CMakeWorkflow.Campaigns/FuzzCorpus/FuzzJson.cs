using System.Text.Json;
using System.Text.Json.Serialization;

namespace CMakeWorkflow.Campaigns;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FuzzRunReport))]
internal partial class FuzzJson : JsonSerializerContext;
