using System.Text.Json.Serialization;

namespace CMakeWorkflow.Campaigns;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CampaignCompletion))]
internal partial class CampaignCompletionJson : JsonSerializerContext;