using System.Text.Json.Serialization;

namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>Content-addressed Project SDK configuration selected for a managed build tree.</summary>
/// <param name="Id">Lowercase SHA-256 of the portable variant identity.</param>
/// <param name="LockSha256">Lowercase SHA-256 of the approved configuration lock.</param>
public sealed record WorkflowVariantBinding(string Id, string LockSha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KitLockSha256 { get; init; }
}
