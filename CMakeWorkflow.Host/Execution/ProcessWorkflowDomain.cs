using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

// Domain-owned input validation, request identity and report interpretation.
internal static class ProcessWorkflowDomain
{
    internal static DurableImmutableRequest Admission(ExecutionStartRequest request)
    {
        if (request.Jobs is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            "execution/start:process:v1", request.Manifest, request.ManifestSha256.ToUpperInvariant(),
            request.Jobs.ToString(System.Globalization.CultureInfo.InvariantCulture), request.ResultsDirectory))));
        return new(request.ClientRequestId, request.Manifest, request.ManifestSha256,
            request.ResultsDirectory, digest, "process");
    }

    internal static ProcessWorkflowManifest ReadPlan(byte[] snapshot, WorkspaceReadPaths paths)
    {
        if (snapshot.Length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("Process manifest exceeds 2 MiB.");
        }

        ProcessWorkflowManifest manifest = JsonSerializer.Deserialize(snapshot, ProcessWorkflowJson.Default.ProcessWorkflowManifest)
                       ?? throw new InvalidDataException("Invalid process execution manifest.");
        if (manifest.Domain != "process" || manifest.SchemaVersion != 1 ||
            manifest.Nodes is not { Count: > 0 and <= 128 })
        {
            throw new InvalidDataException("Unsupported process manifest domain/schema/size.");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (ProcessWorkflowNode node in manifest.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || node.Id.Length > 128 || !ids.Add(node.Id) ||
                string.IsNullOrWhiteSpace(node.Executable) || node.Executable.Length > 4096 ||
                node.Arguments is { Count: > 256 } || node.Dependencies is { Count: > 128 } ||
                node.Resources is { Count: > 128 } || node.TimeoutSeconds is < 1 or > 86400)
            {
                throw new InvalidDataException("Invalid or duplicate process workflow node.");
            }

            if (node.Arguments?.Any(a => a is null) == true ||
                node.Resources?.Any(string.IsNullOrWhiteSpace) == true ||
                node.Dependencies?.Any(string.IsNullOrWhiteSpace) == true ||
                node.Environment?.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value is null) == true)
            {
                throw new InvalidDataException("Invalid process node arguments, resources or environment.");
            }

            if (node.WorkingDirectory is not null)
            {
                string wd = Path.IsPathFullyQualified(node.WorkingDirectory)
                    ? node.WorkingDirectory : Path.Combine(paths.WorkspaceDirectory, node.WorkingDirectory);
                _ = paths.Require(wd);
                if (!Directory.Exists(wd))
                {
                    throw new DirectoryNotFoundException(wd);
                }
            }
        }

        Dictionary<string, int> visited = new(StringComparer.Ordinal);
        Dictionary<string, ProcessWorkflowNode> table = manifest.Nodes.ToDictionary(x => x.Id, StringComparer.Ordinal);
        void Visit(string id)
        {
            if (visited.GetValueOrDefault(id) == 2)
            {
                return;
            }

            if (visited.GetValueOrDefault(id) == 1)
            {
                throw new InvalidDataException("Process workflow dependency cycle.");
            }

            visited[id] = 1;
            foreach (string dep in table[id].Dependencies ?? [])
            {
                if (!table.ContainsKey(dep))
                {
                    throw new InvalidDataException("Unknown process dependency: " + dep);
                }

                Visit(dep);
            }
            visited[id] = 2;
        }
        foreach (ProcessWorkflowNode node in manifest.Nodes)
        {
            Visit(node.Id);
        }

        return manifest;
    }
}

internal sealed class ProcessCheckpointEvidenceCodec : IOperationCheckpointEvidenceCodec
{
    internal static ProcessCheckpointEvidenceCodec Instance { get; } = new();

    internal static ProcessOperationReport Parse(byte[] bytes)
    {
        ProcessOperationReport report = JsonSerializer.Deserialize(bytes, ProcessWorkflowJson.Default.ProcessOperationReport)
                     ?? throw new InvalidDataException("Invalid process operation checkpoint.");
        if (report.Domain != "process" || report.SchemaVersion != 1 ||
            report.SnapshotSha256 is not { Length: 64 } || !report.SnapshotSha256.All(Uri.IsHexDigit) ||
            report.ResumeCount is < 0 or > 1 || report.Nodes is not { Count: > 0 and <= 128 } ||
            report.Status is not ("pending" or "running" or "succeeded" or "failed" or "canceled" or
                "blocked" or "needs_attention" or "outcome_unknown"))
        {
            throw new InvalidDataException("Invalid process checkpoint identity/structure.");
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (ProcessNodeReport? node in report.Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id) ||
                node.Status is not ("pending" or "running" or "succeeded" or "failed" or "canceled" or
                    "blocked" or "needs_attention" or "outcome_unknown"))
            {
                throw new InvalidDataException("Invalid process checkpoint node.");
            }

            if ((node.Status == "succeeded" && (node.ExitCode != 0 || node.StartedAt is null ||
                    node.FinishedAt is null || node.Stdout is null || node.Stderr is null)) ||
                (node.Status == "running" && (node.StartedAt is null || node.Stdout is null || node.Stderr is null)))
            {
                throw new InvalidDataException("Inconsistent process checkpoint result.");
            }
        }
        foreach (ProcessNodeReport node in report.Nodes)
        {
            if (node.Dependencies is { } dependencies && (dependencies.Count > 128 ||
                dependencies.Distinct(StringComparer.Ordinal).Count() != dependencies.Count ||
                dependencies.Any(d => !ids.Contains(d) || d == node.Id)))
            {
                throw new InvalidDataException("Invalid process checkpoint dependency IDs.");
            }
        }

        return report;
    }

    public OperationCheckpointEvidence ForDispatch(byte[] checkpointBytes)
    {
        ProcessOperationReport report = Parse(checkpointBytes);
        return report.Domain != "process" || report.SchemaVersion != 1 || report.Status != "running" ||
            !report.Nodes.Any(n => n.Status == "running")
            ? throw new InvalidDataException("Process checkpoint has no admitted dispatch.")
            : new(report.SnapshotSha256);
    }

    public OperationCheckpointEvidence ForResume(byte[] checkpointBytes)
    {
        ProcessOperationReport report = Parse(checkpointBytes);
        if (report.Status != "needs_attention" || report.RecoveryPhase != "reconciled" ||
            report.ResumeCount != 0 || report.Recovery is not { } audit ||
            string.IsNullOrWhiteSpace(audit.Note) || audit.Note.Length > 4096 || audit.ConfirmedAt == default ||
            audit.PreviousCheckpoint is not { Length: > 0 and <= 4 * 1024 * 1024 } ||
            audit.DispatchCheckpoint is not { Length: > 0 and <= 4 * 1024 * 1024 })
        {
            throw new InvalidOperationException("resume_checkpoint_not_safe");
        }

        ProcessOperationReport prior = Parse(audit.PreviousCheckpoint);
        ProcessOperationReport bound = Parse(audit.DispatchCheckpoint);
        ValidateRecovery(report, prior, bound);
        return new(report.SnapshotSha256, ResumeSourceCheckpointSha256:
            Convert.ToHexString(SHA256.HashData(audit.DispatchCheckpoint)));
    }

    internal static bool Eligible(ProcessNodeReport node) =>
        node.Status is "pending" or "blocked" or "needs_attention";

    private static bool NeverAdmitted(ProcessNodeReport node) => Eligible(node) &&
        node.StartedAt is null && node.FinishedAt is null && node.ExitCode is null &&
        node.Stdout is null && node.Stderr is null;

    internal static void ValidateRecovery(ProcessOperationReport report, ProcessOperationReport prior,
        ProcessOperationReport bound)
    {
        if (prior.Recovery is not null || prior.ResumeCount != 0 || bound.ResumeCount != 0 ||
            bound.Status != "running" || !bound.Nodes.Any(n => n.Status == "running") ||
            !string.Equals(report.SnapshotSha256, prior.SnapshotSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(report.SnapshotSha256, bound.SnapshotSha256, StringComparison.OrdinalIgnoreCase) ||
            !report.Nodes.Select(n => n.Id).SequenceEqual(prior.Nodes.Select(n => n.Id)) ||
            !report.Nodes.Select(n => n.Id).SequenceEqual(bound.Nodes.Select(n => n.Id)))
        {
            throw new InvalidOperationException("process_recovery_identity_mismatch");
        }

        for (int i = 0; i < report.Nodes.Count; ++i)
        {
            ProcessNodeReport node = report.Nodes[i];
            // Recovery never changes a node result. In particular an operator's
            // operation-level decision is not authority to retry a running node.
            if (!JsonSerializer.SerializeToUtf8Bytes(node, ProcessWorkflowJson.Default.ProcessNodeReport)
                    .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(prior.Nodes[i],
                        ProcessWorkflowJson.Default.ProcessNodeReport)) ||
                node.Status is "running" or "outcome_unknown" ||
                (Eligible(node) && (!NeverAdmitted(node) || !NeverAdmitted(bound.Nodes[i]))) ||
                (bound.Nodes[i].Status is "succeeded" or "failed" or "canceled" &&
                !JsonSerializer.SerializeToUtf8Bytes(node, ProcessWorkflowJson.Default.ProcessNodeReport)
                    .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(bound.Nodes[i],
                        ProcessWorkflowJson.Default.ProcessNodeReport))))
            {
                throw new InvalidOperationException("process_recovery_unresolved_or_previously_dispatched");
            }
        }
    }

    public OperationCheckpointEvidence ForFinalization(byte[] checkpointBytes, int exitCode)
    {
        ProcessOperationReport report = Parse(checkpointBytes);
        return report.Domain != "process" || report.SchemaVersion != 1 || report.FinishedAt is null ||
            report.Status is not ("succeeded" or "failed" or "canceled") ||
            report.Nodes.Any(n => n.Status is "pending" or "running" or "outcome_unknown") ||
            (report.Status == "succeeded" && report.Nodes.Any(n => n.Status != "succeeded")) ||
            (report.Status == "succeeded" ? 0 : report.Status == "canceled" ? 130 : 1) != exitCode
            ? throw new InvalidOperationException("process_operation_report_not_terminal")
            : new(report.SnapshotSha256, report.Status, report.ResumeCount);
    }
}
