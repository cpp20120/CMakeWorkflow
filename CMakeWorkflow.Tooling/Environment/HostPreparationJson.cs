using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    Converters =
    [
        typeof(JsonStringEnumConverter<ToolPlatform>),
        typeof(JsonStringEnumConverter<PackageManagerKind>)
    ])]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(HostRequirements))]
[JsonSerializable(typeof(EtwProbeResult))]
[JsonSerializable(typeof(List<HostFinding>))]
[JsonSerializable(typeof(HostPreparationPlan))]
[JsonSerializable(typeof(HostPreparationResult))]
[JsonSerializable(typeof(IReadOnlyList<HostSettingChange>))]
[JsonSerializable(typeof(ProvisioningAuditEntry))]
internal sealed partial class HostPreparationJson : JsonSerializerContext;