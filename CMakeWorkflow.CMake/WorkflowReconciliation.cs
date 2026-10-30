using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed record WorkflowReconciliation(string Id, DateTimeOffset Timestamp, string Actor,
    string Host, string? NodeId, string Decision, string Reason, string CheckpointSha256,
    string PreviousRunStatus, string? PreviousRecoveryPhase, NodeResult? PreviousNode,
    bool ProcessesConfirmedStopped);

public static class WorkflowReconcileCommand
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0) throw new ArgumentException("workflow reconcile requires a run directory.");
        string? nodeId = null, decision = null, reason = null, expected = null;
        bool stopped = false, json = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option)) throw new ArgumentException($"Duplicate option: {option}");
            if (option == "--confirm-stopped") { stopped = true; continue; }
            if (option == "--json") { json = true; continue; }
            if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {option}");
            var value = args[++i];
            switch (option)
            {
                case "--node": nodeId = value; break;
                case "--decision": decision = value; break;
                case "--reason": reason = value; break;
                case "--expected-checkpoint": expected = value; break;
                default: throw new ArgumentException($"Unknown reconciliation option: {option}");
            }
        }
        if (decision is not ("succeeded" or "retry") || string.IsNullOrWhiteSpace(reason) || reason.Length > 4096
            || expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit) || !stopped)
            throw new ArgumentException("Reconciliation requires --decision succeeded|retry, --reason (1..4096 characters), " +
                "--expected-checkpoint SHA256 and --confirm-stopped. Verify external effects and stop old processes first.");

        var directory = Path.GetFullPath(args[0]);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        await using var lease = new FileStream(Path.Combine(directory, ".workflow-run.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "run.json"), ct);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Checkpoint changed; inspect the current report before deciding again.");
        var report = JsonSerializer.Deserialize(bytes, WorkflowJson.Default.WorkflowReport)
            ?? throw new InvalidDataException("Invalid run report.");
        if (report.SchemaVersion != 1 || report.Status != "outcome_unknown" || report.Nodes.Any(n => n.Status == "running"))
            throw new InvalidDataException("Only quarantined outcome_unknown runs can be reconciled; request resume to quarantine an interrupted run first.");
        var node = nodeId is null ? null : report.Nodes.SingleOrDefault(n => n.Id == nodeId)
            ?? throw new ArgumentException("Node does not exist in the saved run.");
        if (node is not null && node.Status != "outcome_unknown")
            throw new InvalidDataException("Only outcome_unknown nodes accept a manual decision.");
        if (node is null && (decision != "retry" || report.Nodes.Any(n => n.Status == "outcome_unknown")))
            throw new InvalidDataException("Run-level retry is only for an interrupted checkpoint with no unknown nodes; reconcile each unknown node first.");

        var plan = await WorkflowPlanner.LoadAsync(report.Workflow, [], null, ct);
        if (plan.ManifestHash != report.ManifestHash || plan.Nodes.Count != report.Nodes.Count
            || plan.Nodes.Where((n, i) => n.Definition.Id != report.Nodes[i].Id || n.Definition.Kind != report.Nodes[i].Kind
                || n.Target.Target != report.Nodes[i].Target || n.Target.Configuration != report.Nodes[i].Configuration
                || !WorkflowPlanner.PathComparer.Equals(n.Target.BuildDirectory, report.Nodes[i].BuildDirectory)
                || !n.Dependencies.SequenceEqual(report.Nodes[i].Dependencies)).Any())
            throw new InvalidDataException("Manifest or graph changed; reconciliation refused.");
        using var ownership = await BuildTreeOwnership.AcquireAsync(
            plan.Nodes.Where(n => n.Definition.Kind != "inspect").Select(n => n.Target.BuildDirectory), ct);
        var audit = new WorkflowReconciliation(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
            Environment.UserName, Environment.MachineName, nodeId, decision!, reason!, hash,
            report.Status, report.RecoveryPhase, node is null ? null : JsonSerializer.Deserialize(
                JsonSerializer.SerializeToUtf8Bytes(node, WorkflowJson.Default.NodeResult), WorkflowJson.Default.NodeResult), true);

        if (node is not null && decision == "succeeded")
        {
            if (node.Dependencies.Any(id => report.Nodes.Single(n => n.Id == id).Status != "succeeded"))
                throw new InvalidDataException("Reconcile successful prerequisites first.");
            var planned = plan.Nodes.Single(n => n.Definition.Id == node.Id);
            var observed = await CMakeInspection.InspectAsync(node.BuildDirectory, ct);
            var session = new BuildContextSession();
            if (node.Kind == "configure") session.Configured(planned.Requested, observed);
            else if (node.Kind != "inspect")
            {
                session.Observe(planned.Requested, observed, "ManualReconciliation");
                session.Validate(planned.Requested, observed);
            }
            node.ObservationAfter = observed;
            node.SessionAfter = session.State(planned.Requested, observed);
            if (node.Kind == "build")
            {
                var target = CMakeInspection.Resolve(BuildContextSession.ResolutionRequest(planned.Requested), observed, node.Target);
                var item = CMakeInspection.RequireTarget(observed, target);
                // An operator attestation is distinct from an observed backend exit code.
                node.BuildVerification = new(target, "ManuallyAccepted", null, observed.IndexFile, GenerationObservation.From(observed));
                if (plan.Nodes.Any(n => n.Definition.From == node.Id))
                {
                    if (item.Type != "EXECUTABLE" || item.Executable is null)
                        throw new InvalidDataException("Cannot reconcile build: dependent execution requires an observed executable artifact.");
                    await using var artifact = File.OpenRead(item.Executable);
                    var sha = Convert.ToHexString(await SHA256.HashDataAsync(artifact, ct));
                    node.Artifact = new(node.Id, target, item.Executable, sha, "ManuallyAccepted");
                    node.ExecutableSha256 = sha;
                }
            }
            node.Status = "succeeded";
            node.Error = null;
            node.ExperimentResult = "ManuallyAccepted";
            // No invented exit code, process duration or completion timestamp.
        }
        else if (node is not null)
        {
            node.Status = "pending";
            node.Error = null;
        }
        report.Reconciliations.Add(audit);
        report.RecoveryPhase = report.Nodes.Any(n => n.Status == "outcome_unknown") ? "reconciling" : "reconciled";
        report.Status = report.RecoveryPhase == "reconciled" ? "needs_attention" : "outcome_unknown";
        report.Error = report.Status == "needs_attention" ? "Manually reconciled; explicit resume is required."
            : "Additional unknown nodes require manual reconciliation.";
        await WorkflowCheckpoint.SaveAsync(report, directory, ct);
        if (json) Console.Out.WriteLine(JsonSerializer.Serialize(report, WorkflowJson.Default.WorkflowReport));
        else Console.Out.WriteLine($"Recorded {decision} for {nodeId ?? "run"}; status={report.Status}. No operations launched.");
        return 0;
    }
}
