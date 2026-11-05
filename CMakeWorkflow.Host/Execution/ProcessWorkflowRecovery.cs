using System.Security.Cryptography;
using System.Text.Json;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

internal static class ProcessWorkflowRecovery
{
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

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
