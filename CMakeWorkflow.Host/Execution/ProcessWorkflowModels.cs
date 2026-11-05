using System.Text.Json.Serialization;

namespace CMakeWorkflow.Host;

// A separate, non-CMake manifest and domain report. No CMake plan or report
// types are involved in executing or interpreting these records.
internal sealed record ProcessWorkflowManifest(int SchemaVersion, string Domain,
    List<ProcessWorkflowNode> Nodes);

internal sealed record ProcessWorkflowNode(string Id, string Executable,
    List<string>? Arguments = null, List<string>? Dependencies = null,
    string? WorkingDirectory = null, Dictionary<string, string>? Environment = null,
    List<string>? Resources = null, bool Exclusive = false, int? TimeoutSeconds = null);

internal sealed class ProcessNodeReport
{
    public required string Id { get; init; }
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public int? ExitCode { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public List<string>? Dependencies { get; init; }
}

internal sealed class ProcessOperationReport
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    [JsonRequired]
    public string Domain { get; init; } = "process";
    public required string SnapshotSha256 { get; init; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public string? RecoveryPhase { get; set; }
    public int ResumeCount { get; set; }
    public ProcessRecoveryAudit? Recovery { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public required List<ProcessNodeReport> Nodes { get; init; }
}

// Exact pre-recovery bytes retain the evidence behind the operator's decision.
// The dispatch hash is checked against SQLite when claiming the single attempt.
internal sealed record ProcessRecoveryAudit(string Note, DateTimeOffset ConfirmedAt,
    byte[] PreviousCheckpoint, byte[] DispatchCheckpoint);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProcessWorkflowManifest))]
[JsonSerializable(typeof(ProcessWorkflowNode))]
[JsonSerializable(typeof(ProcessOperationReport))]
[JsonSerializable(typeof(ProcessNodeReport))]
internal partial class ProcessWorkflowJson : JsonSerializerContext;
