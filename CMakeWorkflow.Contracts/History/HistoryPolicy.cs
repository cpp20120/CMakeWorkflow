using System.Text.Json.Serialization;

namespace CMakeWorkflow.Contracts.History;

/// <summary>Controls post-execution indexing of a durable workflow into a Host history database.</summary>
public sealed record HistoryPolicy(HistoryPolicyMode Mode, string? DatabasePath = null);

[JsonConverter(typeof(JsonStringEnumConverter<HistoryPolicyMode>))]
public enum HistoryPolicyMode
{
    None,
    Disabled,
    Default,
    ExplicitDatabase
}
