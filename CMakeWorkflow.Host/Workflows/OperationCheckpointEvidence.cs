namespace CMakeWorkflow.Host.Workflows;

// Facts validated from a runner-owned checkpoint by a trusted domain codec.
// The Host store binds their originating bytes and operation identity under
// SQLite transactions; it does not interpret a domain-specific report format.
/// <summary>Facts verified from runner-owned bytes by a trusted domain codec.</summary>
/// <remarks>
/// The ledger binds originating bytes and operation identity transactionally; it does not interpret domain reports.
/// Null terminal/resume-source fields mean that evidence is absent, never inferred success or safe retry.
/// </remarks>
internal sealed record OperationCheckpointEvidence(string SnapshotSha256,
    string? TerminalState = null, int ResumeCount = 0, string? ResumeSourceCheckpointSha256 = null);

/// <summary>Validates domain-specific checkpoint evidence for each durable transition.</summary>
/// <remarks>
/// Implementations must reject invalid or incompatible bytes before the ledger commits dispatch, resume or finalization.
/// Parsing must not launch workers or mutate checkpoint files.
/// </remarks>
internal interface IOperationCheckpointEvidenceCodec
{
    /// <summary>Validates write-ahead intent before external launch.</summary>
    OperationCheckpointEvidence ForDispatch(byte[] checkpointBytes);
    /// <summary>Validates reconciled recovery evidence before a permanent attempt claim.</summary>
    OperationCheckpointEvidence ForResume(byte[] checkpointBytes);
    /// <summary>Validates terminal report state against the supplied runner exit code.</summary>
    OperationCheckpointEvidence ForFinalization(byte[] checkpointBytes, int exitCode);
}
