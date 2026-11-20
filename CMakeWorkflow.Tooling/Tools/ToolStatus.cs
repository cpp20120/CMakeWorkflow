using System.Text.Json.Serialization;
namespace CMakeWorkflow.Tooling.Tools;

public sealed record ToolStatus(
    string Id,
    string DisplayName,
    bool Installed,
    string? Command,
    string? Version,
    string? MinimumVersion,
    string? Diagnostic)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Resolution { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Origin { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemResolution { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemVersion { get; init; }
}
