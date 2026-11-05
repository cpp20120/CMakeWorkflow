namespace CMakeWorkflow.Host;

// Facts validated from a runner-owned checkpoint by a trusted domain codec.
// The Host store binds their originating bytes and operation identity under
// SQLite transactions; it does not interpret a domain-specific report format.
internal sealed record OperationCheckpointEvidence(string SnapshotSha256,
    string? TerminalState = null, int ResumeCount = 0);

internal interface IOperationCheckpointEvidenceCodec
{
    OperationCheckpointEvidence ForDispatch(byte[] checkpointBytes);
    OperationCheckpointEvidence ForResume(byte[] checkpointBytes);
    OperationCheckpointEvidence ForFinalization(byte[] checkpointBytes, int exitCode);
}
