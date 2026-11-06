using System.Text.Json.Serialization;

namespace CMakeWorkflow.Host.Execution;

// A separate, non-CMake manifest and domain report. No CMake plan or report
// types are involved in executing or interpreting these records.
/// <summary>Process/schema-1 graph consumed only after process-domain validation.</summary>
/// <remarks>
/// Collections are mutable despite the record shape. The execution adapter treats the accepted graph as immutable;
/// its serialized snapshot, not a later manifest file, is the recovery authority.
/// </remarks>
internal sealed record ProcessWorkflowManifest(int SchemaVersion, string Domain,
    List<ProcessWorkflowNode> Nodes);

/// <summary>Literal process launch request and run-local scheduling constraints.</summary>
/// <remarks>
/// Null arguments, dependencies, environment and resources mean empty collections. Relative working directories
/// use the Host workspace; null selects that workspace. TimeoutSeconds, when present, is 1..86400 seconds.
/// Executable resolution occurs at launch; validation does not establish that a command will start successfully.
/// </remarks>
internal sealed record ProcessWorkflowNode(string Id, string Executable,
    List<string>? Arguments = null, List<string>? Dependencies = null,
    string? WorkingDirectory = null, Dictionary<string, string>? Environment = null,
    List<string>? Resources = null, bool Exclusive = false, int? TimeoutSeconds = null);

/// <summary>Mutable checkpoint evidence for one process node.</summary>
/// <remarks>
/// Reserved on the scheduler path; workers return separate results for serial integration. Running intent includes
/// StartedAt and log names before process launch. Those fields are evidence of possible admission, not confirmed effects.
/// Null exit/timing/log fields represent unavailable evidence and must not be filled speculatively during recovery.
/// </remarks>
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

/// <summary>Runner-owned mutable process checkpoint and terminal report.</summary>
/// <remarks>
/// The run lease serializes cooperating writers. SnapshotSha256 binds the immutable graph; ResumeCount is limited
/// to one by the codec. A terminal report is not a durable operation result until the ledger verifies and finalizes it.
/// </remarks>
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
/// <summary>Retains exact prior and dispatch bytes behind an explicit recovery decision.</summary>
/// <remarks>
/// These byte arrays are evidence, not reconstructed reports. The ledger-bound dispatch hash is rechecked before
/// the single attempt claim. ConfirmedAt records an operator assertion, not automatic detection of quiescence.
/// </remarks>
internal sealed record ProcessRecoveryAudit(string Note, DateTimeOffset ConfirmedAt,
    byte[] PreviousCheckpoint, byte[] DispatchCheckpoint);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProcessWorkflowManifest))]
[JsonSerializable(typeof(ProcessWorkflowNode))]
[JsonSerializable(typeof(ProcessOperationReport))]
[JsonSerializable(typeof(ProcessNodeReport))]
internal partial class ProcessWorkflowJson : JsonSerializerContext;
