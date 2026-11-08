using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.Core.Persistence;
using CMakeWorkflow.Host.Sessions;
using CMakeWorkflow.Host.Workflows;

namespace CMakeWorkflow.Host.Execution;

/// <summary>Validates process recovery evidence without inferring success from interrupted running intents.</summary>
internal static class ProcessWorkflowRecovery
{
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Matches saved process outcomes to the immutable graph and existing run logs.</summary>
    /// <remarks>
    /// Requires the same snapshot hash and ordered node IDs. Saved dependency lists, when present, must match
    /// the manifest; successful nodes must have successful prerequisites. Legacy reports may omit dependency lists.
    /// Recorded logs must be existing leaf files inside the run. This check does not claim resume or prove external quiescence.
    /// </remarks>
    internal static void ValidatePlan(ProcessOperationReport report, ProcessWorkflowManifest manifest,
        string snapshotSha256, WorkspaceReadPaths paths, string output)
    {
        if (!string.Equals(report.SnapshotSha256, snapshotSha256, StringComparison.OrdinalIgnoreCase) ||
            !report.Nodes.Select(n => n.Id).SequenceEqual(manifest.Nodes.Select(n => n.Id)))
        {
            throw new InvalidDataException("Process checkpoint differs from immutable manifest.");
        }

        Dictionary<string, ProcessNodeReport> states = report.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        for (int i = 0; i < manifest.Nodes.Count; ++i)
        {
            ProcessNodeReport node = report.Nodes[i];
            List<string> deps = manifest.Nodes[i].Dependencies ?? [];
            // Older v1 reports omitted dependencies; the immutable snapshot is
            // authoritative for those reports. New reports record them as well.
            if ((node.Dependencies is { } saved && !saved.SequenceEqual(deps)) ||
                (node.Status == "succeeded" && deps.Any(d => states[d].Status != "succeeded")))
            {
                throw new InvalidDataException("Process checkpoint dependencies changed.");
            }

            foreach (string? leaf in new[] { node.Stdout, node.Stderr })
            {
                if (leaf is null)
                {
                    continue;
                }

                if (leaf != Path.GetFileName(leaf) || leaf is "." or ".." ||
                    !File.Exists(paths.Require(Path.Combine(output, leaf))))
                {
                    throw new InvalidDataException("Process checkpoint log is missing or outside the run.");
                }
            }
        }
    }

    // Called only by explicit resume admission under the execution lease, after
    // SQLite reconciliation and operator-confirmed quiescence. No node is reset.
    /// <summary>Persists an explicit recovery annotation without resetting node outcomes or executing commands.</summary>
    /// <remarks>
    /// Caller must hold the execution lease after reconciliation and operator-confirmed quiescence.
    /// This method acquires the run lease, verifies original graph/logs/checkpoint binding and writes durable recovery evidence.
    /// It does not itself admit or claim resume; those remain separate ledger transitions.
    /// </remarks>
    internal static void Prepare(DurableAcceptanceStore store, string operationId, WorkspaceReadPaths paths)
    {
        DurableAcceptanceStore.RecoveryInspection inspection = store.InspectRecovery(operationId);
        if (inspection.Decision != "needs_attention" || inspection.Revision is not (1 or 2) ||
            inspection.TerminalState is not null || inspection.CancellationRequested)
        {
            throw new InvalidOperationException("resume_not_reconciled");
        }

        (string? RunDirectory, string? CheckpointSha256) = store.GetDispatchCheckpoint(operationId)
                    ?? throw new InvalidOperationException("resume_checkpoint_missing");
        string output = paths.Require(RunDirectory);
        using FileStream runLease = new(paths.Require(Path.Combine(output, ".workflow-run.lock")),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string file = paths.Require(Path.Combine(output, "run.json"));
        paths.Read(file);
        byte[] bytes = File.ReadAllBytes(file);
        ProcessOperationReport report = ProcessCheckpointEvidenceCodec.Parse(bytes);
        AcceptedWorkflow accepted = store.Get(operationId) ?? throw new KeyNotFoundException("Unknown operation.");
        ProcessWorkflowManifest manifest = ProcessWorkflowDomain.ReadPlan(store.GetSnapshot(operationId), paths);
        ValidatePlan(report, manifest, accepted.ManifestSha256, paths, output);
        if (report.Recovery is null)
        {
            byte[] dispatchBytes = store.GetProcessDispatchCheckpointBytes(operationId);
            ProcessCheckpointEvidenceCodec.ValidateRecovery(report, report,
                ProcessCheckpointEvidenceCodec.Parse(dispatchBytes));
            report.Recovery = new(inspection.Note!, DateTimeOffset.UtcNow, bytes, dispatchBytes);
            report.Status = "needs_attention";
            report.RecoveryPhase = "reconciled";
            report.FinishedAt = null;
            bytes = JsonSerializer.SerializeToUtf8Bytes(report, ProcessWorkflowJson.Default.ProcessOperationReport);
            _ = ProcessCheckpointEvidenceCodec.Instance.ForResume(bytes);
            _ = paths.Require(file + ".tmp");
            DurableByteWriter.WriteAsync(file, bytes, CancellationToken.None).GetAwaiter().GetResult();
        }
        OperationCheckpointEvidence evidence = ProcessCheckpointEvidenceCodec.Instance.ForResume(bytes);
        if (report.Recovery!.Note != inspection.Note ||
            !string.Equals(evidence.ResumeSourceCheckpointSha256, CheckpointSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("process_recovery_binding_changed");
        }
    }
}
