using System.Text.Json.Serialization;

namespace CMakeWorkflow.Developer;

internal sealed record ScaffoldIdentity(int SchemaVersion, string Name);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScaffoldIdentity))]
internal sealed partial class DeveloperJson : JsonSerializerContext;
