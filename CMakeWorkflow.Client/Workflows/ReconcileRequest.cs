using System.Text.Json.Serialization;

namespace CMakeWorkflow.Client.Workflows;

internal sealed record ReconcileRequest(
    string OperationId,
    long ExpectedRevision,
    string Decision,
    string Note,
    bool ConfirmedQuiescent)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RetryBinaryNode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpectedCheckpointSha256 { get; init; }
}
