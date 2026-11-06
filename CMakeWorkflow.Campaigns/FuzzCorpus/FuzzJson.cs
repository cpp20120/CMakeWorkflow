using System.Text.Json.Serialization;

namespace CMakeWorkflow.Campaigns.FuzzCorpus;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FuzzRunReport))]
internal partial class FuzzJson : JsonSerializerContext;
