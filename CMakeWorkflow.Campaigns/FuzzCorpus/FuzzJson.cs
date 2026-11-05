using System.Text.Json.Serialization;
using CMakeWorkflow.Campaigns.FuzzCorpus;

namespace CMakeWorkflow.Campaigns;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FuzzRunReport))]
internal partial class FuzzJson : JsonSerializerContext;
