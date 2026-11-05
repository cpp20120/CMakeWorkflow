using System.Text.Json;
using CMakeWorkflow.CMake.Reporting;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

// Compatibility adapter for the existing run.json report. All checks remain
// byte-for-byte compatible with the old DurableAcceptanceStore call sites.
internal sealed class CMakeWorkflowCheckpointEvidenceCodec : IOperationCheckpointEvidenceCodec
{
    internal static CMakeWorkflowCheckpointEvidenceCodec Instance { get; } = new();

    public OperationCheckpointEvidence ForDispatch(byte[] checkpointBytes)
    {
        WorkflowReport report = JsonSerializer.Deserialize(checkpointBytes, WorkflowJson.Default.WorkflowReport)
                     ?? throw new InvalidDataException("Invalid runner checkpoint.");
        return report.Status != "running" || !report.Nodes.Any(n => n.Status == "running")
            ? throw new InvalidDataException("Runner checkpoint has no admitted dispatch.")
            : new(report.ManifestHash);
    }

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
