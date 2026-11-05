using System.Text.Json;

namespace CMakeWorkflow;

public static class WorkflowRunner
{
    public static async Task<int> RunAsync(WorkflowPlan plan, int jobs, string output, CancellationToken ct,
        IWorkflowOperationExecutor? extension = null, bool eventsToStdout = false, bool resume = false,
        Func<string, CancellationToken, Task>? beforeExternalDispatch = null,
        IReadOnlyDictionary<string, WorkflowToolchainRuntime>? toolchains = null)
    {
        toolchains ??= new Dictionary<string, WorkflowToolchainRuntime>(WorkflowPlanner.PathComparer);
        if (plan.Nodes.Any(n => n.Toolchain is not null && n.Definition.Kind != "inspect"
                                                        && !toolchains.ContainsKey(n.Target.BuildDirectory)))
            throw new InvalidOperationException("Selected toolchain was not materialized before workflow admission.");
        // The run lease spans validation, the recovery checkpoint, execution and final persistence.
        // No second runner may inspect/commit the same persisted run concurrently.
        Directory.CreateDirectory(output);
        await using var runLease = new FileStream(Path.Combine(output, ".workflow-run.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Validate resume observations under the same ownership used for dispatch.
        // Otherwise another cooperating runner can change the tree in between.
        using var resumedOwnership = resume
            ? await BuildTreeOwnership.AcquireAsync(
                plan.Nodes.Where(n => n.Definition.Kind != "inspect").Select(n => n.Target.BuildDirectory), ct)
            : null;
        var report = new WorkflowReport
        {
            Workflow = plan.File,
            ManifestHash = plan.ManifestHash,
            Toolchains = WorkflowToolchainAdmission.Identities(toolchains),
            Nodes = plan.Nodes.Select(n => new NodeResult
            {
                Id = n.Definition.Id, Kind = n.Definition.Kind,
                Dependencies = n.Dependencies.ToList(), BuildDirectory = n.Target.BuildDirectory,
                Configuration = n.Target.Configuration, Target = n.Target.Target,
                WorkingDirectory = n.WorkingDirectory, RequestedContext = n.Requested
            }).ToList()
        };
        if (resume)
        {
            var file = Path.Combine(output, "run.json");
            if (!File.Exists(file)) throw new InvalidDataException("Run report not found for resume.");
            var saved = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(file, ct),
                            WorkflowJson.Default.WorkflowReport)
                        ?? throw new InvalidDataException("Invalid resume report.");
            if (saved.SchemaVersion != 1 || saved.Status is not ("needs_attention" or "running") ||
                saved.ManifestHash != plan.ManifestHash ||
                !WorkflowPlanner.PathComparer.Equals(
                    BuildTreeOwnership.CanonicalPath(saved.Workflow), BuildTreeOwnership.CanonicalPath(plan.File)))
                throw new InvalidDataException(
                    "Resume requires a needs_attention checkpoint with identical workflow and manifest; interrupted running checkpoints can only be quarantined.");
            if (saved.Nodes.Count != report.Nodes.Count ||
                saved.Nodes.Where((n, i) => n.Id != report.Nodes[i].Id || n.Kind != report.Nodes[i].Kind ||
                                            n.Target != report.Nodes[i].Target ||
                                            !WorkflowPlanner.PathComparer.Equals(
                                                BuildTreeOwnership.CanonicalPath(n.BuildDirectory),
                                                BuildTreeOwnership.CanonicalPath(report.Nodes[i].BuildDirectory)) ||
                                            n.Configuration != report.Nodes[i].Configuration ||
                                            !n.Dependencies.SequenceEqual(report.Nodes[i].Dependencies)).Any())
                throw new InvalidDataException("Resume graph differs from saved execution.");
            WorkflowToolchainAdmission.RequireSameIdentity(saved.Toolchains, toolchains);
            // A write-ahead admission is not proof that a process started, nor
            // that it did not produce side effects. Classify before validating
            // old build/artifact observations: an interrupted operation may have
            // legitimately changed those. Never restore sessions or dispatch here.
            if (saved.Status == "running" || saved.Nodes.Any(n => n.Status is "running" or "outcome_unknown") ||
                saved.RecoveryPhase is "resuming" or "running")
            {
                foreach (var node in saved.Nodes.Where(n => n.Status == "running"))
                {
                    node.Status = "outcome_unknown";
                    node.Error =
                        "Runner interrupted after dispatch admission; external side effects cannot be ruled out.";
                }

                saved.Status = "outcome_unknown";
                saved.Error = "Interrupted execution requires manual reconciliation; automatic resume is prohibited.";
                saved.RecoveryPhase = "interrupted";
                await WorkflowCheckpoint.SaveAsync(saved, output, ct);
                Console.Error.WriteLine($"[CMakeWorkflow] {saved.Error}");
                return 4;
            }

            // Compare the latest successful observation for each physical tree;
            // earlier configure/build nodes may legitimately observe older generations.
            foreach (var tree in saved.Nodes.Where(n => n.Status == "succeeded" && n.Kind != "inspect")
                         .GroupBy(n => n.BuildDirectory, WorkflowPlanner.PathComparer))
            {
                var last = tree.LastOrDefault(n => n.ObservationAfter is not null || n.ObservedContext is not null);
                var expected = last?.ObservationAfter ?? last?.ObservedContext;
                if (expected is null) throw new InvalidDataException($"Missing tree observation for '{tree.Key}'.");
                var observed = await CMakeInspection.InspectAsync(tree.Key, ct);
                if (GenerationObservation.From(expected) != GenerationObservation.From(observed) ||
                    expected.SourceDirectory != observed.SourceDirectory ||
                    expected.Generator != observed.Generator ||
                    expected.MultiConfig != observed.MultiConfig ||
                    !expected.Configurations.SequenceEqual(observed.Configurations))
                    throw new InvalidDataException($"Build tree changed since node '{last!.Id}'; resume refused.");
            }

            foreach (var node in saved.Nodes.Where(n => n.Status == "succeeded"))
            {
                if (node.Artifact is { } artifact)
                {
                    if (!File.Exists(artifact.Executable) ||
                        await CMakeWorkflowNodeExecutor.HashExecutableAsync(artifact.Executable, ct) != artifact.Sha256)
                        throw new InvalidDataException($"Artifact changed since node '{node.Id}'; resume refused.");
                }

                foreach (var evidence in node.DeclaredArtifacts)
                    if (await WorkflowArtifacts.HashAsync(evidence.Path, ct) != evidence.Sha256
                        || evidence.SnapshotPath is { } snapshot
                        && await WorkflowArtifacts.HashAsync(snapshot, ct) != evidence.Sha256)
                        throw new InvalidDataException(
                            $"Declared artifact changed since node '{node.Id}'; resume refused.");
            }

            saved.ResumeCount++;
            saved.RecoveryPhase = "resuming";
            saved.Status = "running";
            saved.FinishedAt = null;
            saved.Error = null;
            saved.Findings.Clear();
            foreach (var node in saved.Nodes.Where(n => n.Status != "succeeded"))
            {
                // A run can only be resumed after a pre-execution admission failure.
                // Downstream nodes were never launched.
                if (node.Status is not ("needs_attention" or "pending" or "blocked"))
                    throw new InvalidDataException($"Unexpected non-resumable node '{node.Id}': {node.Status}.");
                node.Status = "pending";
                node.Error = null;
                node.StartedAt = null;
                node.FinishedAt = null;
            }

            report = saved;
        }

        try
        {
            report.Environment = EnvironmentProvenance.CaptureHost();
        }
        catch (IOException error)
        {
            Console.Error.WriteLine($"[CMakeWorkflow] Environment provenance unavailable: {error.Message}");
        }
        catch (UnauthorizedAccessException error)
        {
            Console.Error.WriteLine($"[CMakeWorkflow] Environment provenance unavailable: {error.Message}");
        }

        var states = report.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var sessions = plan.Nodes.Select(n => n.Target.BuildDirectory).Distinct(WorkflowPlanner.PathComparer)
            .ToDictionary(p => p, _ => new BuildContextSession(), WorkflowPlanner.PathComparer);
        var remaining = plan.Nodes.Where(n => states[n.Definition.Id].Status != "succeeded").ToList();
        if (resume)
        {
            // Restore per-tree session admission state from the verified observations.
            foreach (var node in plan.Nodes.Where(n => states[n.Definition.Id].Status == "succeeded"))
            {
                var saved = states[node.Definition.Id];
                var observed = saved.ObservationAfter ?? saved.ObservedContext;
                if (observed is null || node.Definition.Kind == "inspect") continue;
                var session = sessions[node.Target.BuildDirectory];
                if (node.Definition.Kind == "configure") session.Configured(node.Requested, observed);
                else session.Observe(node.Requested, observed, "Resume");
            }
        }

        var active = new Dictionary<string, (PlannedNode Node, Task<NodeResult> Task)>(StringComparer.Ordinal);
        BuildTreeOwnership? ownership = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var events = new WorkflowEventJournal(output, eventsToStdout, resume ? report : null);
        await SaveAsync(report, output, events);
        try
        {
            var mutationTrees = plan.Nodes.Where(n => n.Definition.Kind != "inspect")
                .Select(n => n.Target.BuildDirectory).ToArray();
            if (mutationTrees.Length != 0 && !resume)
                ownership = await BuildTreeOwnership.AcquireAsync(mutationTrees, stop.Token);

            while (remaining.Count > 0 || active.Count > 0)
            {
                if (report.Nodes.Any(n => n.Status == "needs_attention") && active.Count == 0)
                    break;
                foreach (var node in remaining.ToArray())
                {
                    var state = states[node.Definition.Id];
                    if (ct.IsCancellationRequested)
                    {
                        state.Status = "canceled";
                        state.Error = "Workflow canceled before this node started.";
                        remaining.Remove(node);
                    }
                    else if (node.Dependencies.Any(d => states[d].Status is "failed" or "blocked" or "canceled"))
                    {
                        state.Status = "blocked";
                        state.Error = "A prerequisite did not succeed.";
                        remaining.Remove(node);
                    }
                }

                var ready = remaining.Where(n => n.Dependencies.All(d => states[d].Status == "succeeded")).ToArray();
                var exclusive = ready.FirstOrDefault(n => n.Definition.Exclusive);
                // Once an exclusive node is ready, drain existing work before starting it.
                var candidates = exclusive is not null ? new[] { exclusive } : ready;
                // Reserve admission slots before launching anything. A persisted `running`
                // state is the write-ahead intent: after a crash its outcome is unknown.
                var dispatch = new List<(PlannedNode Node, bool Snapshot)>();
                foreach (var node in candidates)
                {
                    if (ct.IsCancellationRequested || active.Count + dispatch.Count >= jobs ||
                        report.Nodes.Any(n => n.Status == "needs_attention")) break;
                    if (active.Values.Any(a => Conflicts(node, a.Node)) ||
                        dispatch.Any(a => Conflicts(node, a.Node))) continue;
                    await RecoveryFaults.HitAsync("before-dispatch", node.Definition.Id, ct);
                    var state = states[node.Definition.Id];
                    state.Status = "running";
                    state.StartedAt = DateTimeOffset.UtcNow;
                    var logStem = LogStem(report, node.Definition.Id);
                    state.Stdout = logStem + ".stdout.log";
                    state.Stderr = logStem + ".stderr.log";
                    remaining.Remove(node);
                    Console.Error.WriteLine($"[CMakeWorkflow] START {node.Definition.Id}");
                    // Execution works on a separate record; report snapshots are written
                    // only by the scheduler, never concurrently with worker mutations.
                    // Snapshot bytes only for builds that have a selected dependent run.
                    // Build-only workflows need not hash potentially large binaries.
                    var snapshot = node.Definition.Kind == "build" && plan.Nodes.Any(p =>
                        p.Definition.Kind is "run" or "profile" && p.Definition.From == node.Definition.Id);
                    dispatch.Add((node, snapshot));
                }

                await SaveAsync(report, output, events);
                // The optional host-owned dispatch gate runs only after the runner's
                // write-ahead checkpoint is durably committed, and strictly before
                // any selected node starts an external process. CLI runs have no gate.
                if (dispatch.Count != 0 && beforeExternalDispatch is not null)
                    await beforeExternalDispatch(output, stop.Token);
                foreach (var (node, snapshot) in dispatch)
                {
                    await RecoveryFaults.HitAsync("after-checkpoint", node.Definition.Id, ct);
                    var sources = node.Definition.Inputs.Where(x => x.From is not null)
                        .Select(x => x.From!).Distinct(StringComparer.Ordinal)
                        .ToDictionary(id => id, id => states[id], StringComparer.Ordinal);
                    active.Add(node.Definition.Id, (node, CMakeWorkflowNodeExecutor.ExecuteAsync(node,
                        node.Definition.From is { } source ? states[source] : null,
                        snapshot, sessions[node.Target.BuildDirectory], output, LogStem(report, node.Definition.Id),
                        stop.Token, extension,
                        toolchains.GetValueOrDefault(node.Target.BuildDirectory), plan.File, sources)));
                }

                if (active.Count == 0)
                {
                    if (report.Nodes.Any(n => n.Status == "needs_attention")) break;
                    if (remaining.Count > 0)
                        throw new InvalidOperationException("No schedulable workflow node.");
                    break;
                }

                await Task.WhenAny(active.Values.Select(a => a.Task));
                foreach (var pair in active.Where(p => p.Value.Task.IsCompleted).ToArray())
                {
                    var result = await pair.Value.Task;
                    await RecoveryFaults.HitAsync("after-execution", pair.Key, ct);
                    states[pair.Key] = result;
                    report.Nodes[report.Nodes.FindIndex(n => n.Id == pair.Key)] = result;
                    active.Remove(pair.Key);
                    if (result.Status == "needs_attention")
                    {
                        report.Findings.Add(new WorkflowFinding("TOOL_EXECUTABLE_MISSING", "error", pair.Key,
                            result.Error ?? "Required executable is unavailable.",
                            result.Kind == "ctest" ? ["cmake"] : ["native"]));
                        // Independent work already launched is allowed to complete; no
                        // additional nodes start after this finding.
                    }

                    Console.Error.WriteLine($"[CMakeWorkflow] {result.Status.ToUpperInvariant()} {pair.Key}" +
                                            (result.Error is null ? "" : $": {result.Error}"));
                }

                await SaveAsync(report, output, events);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var node in report.Nodes.Where(n => n.Status == "pending"))
                node.Status = "canceled";
        }
        catch (Exception error)
        {
            report.Error = error.Message;
            Console.Error.WriteLine($"[CMakeWorkflow] Workflow infrastructure failed: {error.Message}");
            foreach (var node in report.Nodes.Where(n => n.Status == "pending"))
            {
                node.Status = "blocked";
                node.Error = "Workflow infrastructure failed: " + error.Message;
            }
        }
        finally
        {
            // On report/IO failures, never leave launched processes or tree locks behind.
            await stop.CancelAsync();
            foreach (var pair in active)
            {
                var result = await pair.Value.Task;
                report.Nodes[report.Nodes.FindIndex(n => n.Id == pair.Key)] = result;
            }

            ownership?.Dispose();
        }

        report.FinishedAt = DateTimeOffset.UtcNow;
        report.RecoveryPhase = resume ? "finished" : report.RecoveryPhase;
        // An admission whose persistence failed must never appear as safely
        // replayable. No new worker starts before SaveAsync succeeds.
        if (report.Nodes.Any(n => n.Status == "running"))
        {
            foreach (var node in report.Nodes.Where(n => n.Status == "running"))
            {
                node.Status = "outcome_unknown";
                node.Error = "Admission did not commit reliably; manual reconciliation is required.";
            }

            report.RecoveryPhase = "interrupted";
        }

        report.Status = report.Nodes.Any(n => n.Status == "outcome_unknown") ? "outcome_unknown"
            : ct.IsCancellationRequested ? "canceled"
            : report.Nodes.Any(n => n.Status == "needs_attention") ? "needs_attention"
            : report.Error is null && report.Nodes.All(n => n.Status == "succeeded") ? "succeeded" : "failed";
        try
        {
            report.EnvironmentAtEnd = EnvironmentProvenance.CaptureHost();
            report.BuildEnvironments = report.Nodes.Select(n => (n.BuildDirectory, n.Configuration))
                .Where(x => !string.IsNullOrWhiteSpace(x.BuildDirectory)).Distinct()
                .Select(x => EnvironmentProvenance.CaptureBuild(x.BuildDirectory, x.Configuration))
                .OfType<BuildEnvironment>().ToList();
        }
        catch (IOException error)
        {
            Console.Error.WriteLine($"[CMakeWorkflow] Environment provenance unavailable: {error.Message}");
        }
        catch (UnauthorizedAccessException error)
        {
            Console.Error.WriteLine($"[CMakeWorkflow] Environment provenance unavailable: {error.Message}");
        }

        await SaveAsync(report, output, events);
        return report.Status == "canceled" ? 130
            : report.Status == "succeeded" ? 0
            : report.Status == "needs_attention" ? 3
            : report.Status == "outcome_unknown" ? 4 : 1;
    }

    private static bool Conflicts(PlannedNode left, PlannedNode right) =>
        left.Definition.Exclusive || right.Definition.Exclusive
                                  || WorkflowPlanner.PathComparer.Equals(left.Target.BuildDirectory,
                                      right.Target.BuildDirectory)
                                  || left.Definition.Resources
                                      .Intersect(right.Definition.Resources, StringComparer.Ordinal).Any();

    private static string LogStem(WorkflowReport report, string nodeId)
    {
        var retries = report.Reconciliations.Count(r => r.NodeId == nodeId && r.Decision == "retry");
        return retries == 0 ? nodeId : $"{nodeId}.attempt-{retries + 1}";
    }

    private static async Task SaveAsync(WorkflowReport report, string output, WorkflowEventJournal events)
    {
        await WorkflowCheckpoint.SaveAsync(report, output, CancellationToken.None);
        events.Publish(report);
    }
}