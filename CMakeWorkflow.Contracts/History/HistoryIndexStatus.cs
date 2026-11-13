using System.Text.Json.Serialization;

namespace CMakeWorkflow.Contracts.History;

/// <summary>Indexing progress for an operation, independent of its execution result.</summary>
public sealed record HistoryIndexStatus(
    string OperationId,
    HistoryIndexState State,
    string? DatabasePath,
    DateTimeOffset? UpdatedAt,
    string? DiagnosticCode);

[JsonConverter(typeof(JsonStringEnumConverter<HistoryIndexState>))]
public enum HistoryIndexState
{
    [JsonStringEnumMemberName("disabled")]
    Disabled,
    [JsonStringEnumMemberName("awaitingFinalization")]
    AwaitingFinalization,
    [JsonStringEnumMemberName("pending")]
    Pending,
    [JsonStringEnumMemberName("indexing")]
    Indexing,
    [JsonStringEnumMemberName("indexed")]
    Indexed,
    [JsonStringEnumMemberName("failed")]
    Failed
}
