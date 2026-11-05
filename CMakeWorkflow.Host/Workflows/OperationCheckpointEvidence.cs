#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

// Facts validated from a runner-owned checkpoint by a trusted domain codec.
// The Host store binds their originating bytes and operation identity under
// SQLite transactions; it does not interpret a domain-specific report format.
internal sealed record OperationCheckpointEvidence(string SnapshotSha256,
    string? TerminalState = null, int ResumeCount = 0, string? ResumeSourceCheckpointSha256 = null);

internal interface IOperationCheckpointEvidenceCodec
{
    OperationCheckpointEvidence ForDispatch(byte[] checkpointBytes);
    OperationCheckpointEvidence ForResume(byte[] checkpointBytes);
    OperationCheckpointEvidence ForFinalization(byte[] checkpointBytes, int exitCode);
}
