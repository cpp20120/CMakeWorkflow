using System.Text.Json.Serialization;

namespace CMakeWorkflow.Tooling.Tools;

[JsonConverter(typeof(JsonStringEnumConverter<ToolResolutionPolicy>))]
public enum ToolResolutionPolicy
{
    AllowSystem,
    ManagedOnly
}
