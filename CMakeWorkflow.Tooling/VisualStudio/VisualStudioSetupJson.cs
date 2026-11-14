using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling.VisualStudio;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VisualStudioSetupPlan))]
[JsonSerializable(typeof(VisualStudioSetupResult))]
public partial class VisualStudioSetupJson : JsonSerializerContext;
