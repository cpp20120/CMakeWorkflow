using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling.Tools;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters =
    [
        typeof(JsonStringEnumConverter<ToolPlatform>),
        typeof(JsonStringEnumConverter<PackageManagerKind>)
    ])]
[JsonSerializable(typeof(ToolingPlan))]
[JsonSerializable(typeof(ToolingResult))]
[JsonSerializable(typeof(ToolingProfileList))]
public partial class ToolingJson : JsonSerializerContext;