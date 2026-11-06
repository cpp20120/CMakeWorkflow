using System.Text.Json;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Serialization;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

// Compatibility adapter for the existing run.json report. All checks remain
// byte-for-byte compatible with the old DurableAcceptanceStore call sites.
/// <summary>Stateless compatibility adapter for the existing CMake run.json evidence checks.</summary>
/// <remarks>
/// The codec does not acquire leases or bind an operation ID. The ledger verifies returned snapshot identity
/// and exact checkpoint bytes; the runner performs graph, build-tree and toolchain recovery validation separately.
/// </remarks>
internal sealed class CMakeWorkflowCheckpointEvidenceCodec : IOperationCheckpointEvidenceCodec
{
    internal static CMakeWorkflowCheckpointEvidenceCodec Instance { get; } = new();

    /// <inheritdoc/>
    /// <remarks>
    /// Requires report status running and at least one node with running intent; does not prove a child started.
    /// </remarks>
    public OperationCheckpointEvidence ForDispatch(byte[] checkpointBytes)
    {
        WorkflowReport report = JsonSerializer.Deserialize(checkpointBytes, WorkflowJson.Default.WorkflowReport)
                     ?? throw new InvalidDataException("Invalid runner checkpoint.");
        return report.Status != "running" || !report.Nodes.Any(n => n.Status == "running")
            ? throw new InvalidDataException("Runner checkpoint has no admitted dispatch.")
            : new(report.ManifestHash);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Requires needs_attention, no running/resuming phase and only succeeded, needs_attention, pending or
    /// blocked nodes. This check alone does not validate current build trees or claim a resume attempt.
    /// </remarks>
    public OperationCheckpointEvidence ForResume(byte[] checkpointBytes)
    {
        WorkflowReport report = JsonSerializer.Deserialize(checkpointBytes, WorkflowJson.Default.WorkflowReport)
                     ?? throw new InvalidDataException("Invalid resume checkpoint.");
        return report.Status != "needs_attention" || report.RecoveryPhase is "running" or "resuming" ||
            report.Nodes.Any(n => n.Status is "running" or "outcome_unknown") ||
            report.Nodes.Any(n => n.Status is not ("succeeded" or "needs_attention" or "pending" or "blocked"))
            ? throw new InvalidOperationException("resume_checkpoint_not_safe")
            : new(report.ManifestHash);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Requires a finish timestamp and succeeded/failed/canceled report status matching exit code 0/1/130.
    /// These compatibility checks do not perform the process-domain codec's stronger per-node terminal validation.
    /// </remarks>
    public OperationCheckpointEvidence ForFinalization(byte[] checkpointBytes, int exitCode)
    {
        WorkflowReport report = JsonSerializer.Deserialize(checkpointBytes, WorkflowJson.Default.WorkflowReport)
                     ?? throw new InvalidDataException("Invalid final runner report.");
        if (report.FinishedAt is null || report.Status is not ("succeeded" or "failed" or "canceled"))
        {
            throw new InvalidOperationException("runner_report_not_terminal");
        }

        int expectedExit = report.Status switch { "succeeded" => 0, "canceled" => 130, _ => 1 };
        return expectedExit != exitCode
            ? throw new InvalidOperationException("runner_exit_report_mismatch")
            : new(report.ManifestHash, report.Status, report.ResumeCount);
    }
}
