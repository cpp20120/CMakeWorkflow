using System.Text.Json;
using CMakeWorkflow.CMake.Artifacts;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Toolchains;
using CMakeWorkflow.Core;
using CMakeWorkflow.Core.Execution;

namespace CMakeWorkflow.CMake.Execution;

/// <summary>Owns a CMake run lease, DAG scheduling and durable run checkpoints.</summary>
/// <remarks>
/// The Host/CLI caller owns outer operation admission and finalization; this runner owns run-file persistence
/// and domain execution resources. Events are audit/progress evidence, not recovery authority.
/// Interrupted running intents are quarantined as outcome_unknown rather than automatically re-executed.
/// </remarks>
public static class WorkflowRunner
{
    /// <summary>Executes a planned DAG with write-ahead persistence before external dispatch.</summary>
    /// <remarks>
    /// Holds the results run lease across validation, recovery, execution and final persistence.
    /// Each batch records running intents durably before invoking the optional Host gate and any worker.
    /// On cancellation/failure, worker cleanup precedes normal lease release; exceptions do not imply a verified terminal report.
    /// Resume validates saved graph, manifest and build/toolchain evidence; uncertain running checkpoints cannot authorize replay.
    /// </remarks>
    /// <param name="plan">Validated plan and observed context; no unresolved admission errors should remain.</param>
    /// <param name="jobs">Positive maximum concurrently admitted workflow nodes.</param>
    /// <param name="output">Run results directory; created if needed and protected by an exclusive run lease.</param>
    /// <param name="ct">Execution lifetime token; durable callers pass a coordinator token, never the start RPC token.</param>
    /// <param name="extension">Optional executor for extension node kinds such as profile; null supplies no extension.</param>
    /// <param name="eventsToStdout">Whether to also publish journal events to standard output.</param>
    /// <param name="resume">Whether to validate and recover an existing checkpoint instead of initializing a new run.</param>
    /// <param name="beforeExternalDispatch">Optional post-checkpoint authorization callback, awaited before workers start; null omits the outer gate.</param>
    /// <param name="toolchains">Materialized Host toolchain runtimes; null uses an empty binding map.</param>
    /// <returns>Runner status code: 0 succeeded, 1 failed, 130 canceled; recovery refusal/uncertainty uses other nonterminal codes.</returns>
    public static async Task<int> RunAsync(WorkflowPlan plan, int jobs, string output, CancellationToken ct,
        IWorkflowOperationExecutor? extension = null, bool eventsToStdout = false, bool resume = false,
        Func<string, CancellationToken, Task>? beforeExternalDispatch = null,
        IReadOnlyDictionary<string, WorkflowToolchainRuntime>? toolchains = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        toolchains ??= new Dictionary<string, WorkflowToolchainRuntime>(WorkflowPlanner.PathComparer);
        if (plan.Nodes.Any(n => n.Toolchain is not null && n.Definition.Kind != "inspect"
                                                        && !toolchains.ContainsKey(n.Target.BuildDirectory)))
        {
            throw new InvalidOperationException("Selected toolchain was not materialized before workflow admission.");
        }
        // The run lease spans validation, the recovery checkpoint, execution and final persistence.
        // No second runner may inspect/commit the same persisted run concurrently.
        _ = Directory.CreateDirectory(output);
        await using FileStream runLease = new(Path.Combine(output, ".workflow-run.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Validate resume observations under the same ownership used for dispatch.
        // Otherwise another cooperating runner can change the tree in between.
        using BuildTreeOwnership? resumedOwnership = resume
            ? await BuildTreeOwnership.AcquireAsync(
                plan.Nodes.Where(n => n.Definition.Kind is not ("inspect" or "profile_snapshot")).Select(n => n.Target.BuildDirectory), ct)
            : null;
        WorkflowReport report = new()
        {
            Workflow = plan.File,
            ManifestHash = plan.ManifestHash,
            Toolchains = WorkflowToolchainAdmission.Identities(toolchains),
            Nodes = [.. plan.Nodes.Select(n => new NodeResult
            {
                Id = n.Definition.Id,
                Kind = n.Definition.Kind,
                Dependencies = [.. n.Dependencies],
                BuildDirectory = n.Target.BuildDirectory,
                Configuration = n.Target.Configuration,
                Target = n.Target.Target,
                WorkingDirectory = n.WorkingDirectory,
                RequestedContext = n.Requested
            })]
        };
        if (resume)
        {
            string file = Path.Combine(output, "run.json");
            if (!File.Exists(file))
            {
                throw new InvalidDataException("Run report not found for resume.");
            }

            WorkflowReport saved = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(file, ct),
                            Serialization.WorkflowJson.Default.WorkflowReport)
                        ?? throw new InvalidDataException("Invalid resume report.");
            if (saved.SchemaVersion != 1 || saved.Status is not ("needs_attention" or "running") ||
                saved.ManifestHash != plan.ManifestHash ||
                !WorkflowPlanner.PathComparer.Equals(
                    BuildTreeOwnership.CanonicalPath(saved.Workflow), BuildTreeOwnership.CanonicalPath(plan.File)))
            {
                throw new InvalidDataException(
                    "Resume requires a needs_attention checkpoint with identical workflow and manifest; interrupted running checkpoints can only be quarantined.");
            }

            if (saved.Nodes.Count != report.Nodes.Count ||
                saved.Nodes.Where((n, i) => n.Id != report.Nodes[i].Id || n.Kind != report.Nodes[i].Kind ||
                                            n.Target != report.Nodes[i].Target ||
                                            !WorkflowPlanner.PathComparer.Equals(
                                                BuildTreeOwnership.CanonicalPath(n.BuildDirectory),
                                                BuildTreeOwnership.CanonicalPath(report.Nodes[i].BuildDirectory)) ||
                                            n.Configuration != report.Nodes[i].Configuration ||
                                            !n.Dependencies.SequenceEqual(report.Nodes[i].Dependencies)).Any())
            {
                throw new InvalidDataException("Resume graph differs from saved execution.");
            }

            WorkflowToolchainAdmission.RequireSameIdentity(saved.Toolchains, toolchains);
            // A write-ahead admission is not proof that a process started, nor
            // that it did not produce side effects. Classify before validating
            // old build/artifact observations: an interrupted operation may have
            // legitimately changed those. Never restore sessions or dispatch here.
            if (saved.Status == "running" || saved.Nodes.Any(n => n.Status is "running" or "outcome_unknown") ||
                saved.RecoveryPhase is "resuming" or "running")
            {
                foreach (NodeResult? node in saved.Nodes.Where(n => n.Status == "running"))
                {
                    node.Status = "outcome_unknown";
                    node.Error =
                        "Runner interrupted after dispatch admission; external side effects cannot be ruled out.";
                }

                saved.Status = "outcome_unknown";
                saved.Error = "Interrupted execution requires manual reconciliation; automatic resume is prohibited.";
                saved.RecoveryPhase = "interrupted";
                await WorkflowCheckpoint.SaveAsync(saved, output, ct);
                await Console.Error.WriteLineAsync($"[CMakeWorkflow] {saved.Error}");
                return 4;
            }

            // Compare the latest successful observation for each physical tree;
            // earlier configure/build nodes may legitimately observe older generations.
            foreach (IGrouping<string, NodeResult> tree in saved.Nodes.Where(n => n.Status == "succeeded" && n.Kind is not ("inspect" or "profile_snapshot"))
                         .GroupBy(n => n.BuildDirectory, WorkflowPlanner.PathComparer))
            {
                NodeResult? last = tree.LastOrDefault(n => n.ObservationAfter is not null || n.ObservedContext is not null);
                ObservedBuildContext expected = (last?.ObservationAfter ?? last?.ObservedContext) ?? throw new InvalidDataException($"Missing tree observation for '{tree.Key}'.");
                ObservedBuildContext observed = await CMakeInspection.InspectAsync(tree.Key, ct);
                if (GenerationObservation.From(expected) != GenerationObservation.From(observed) ||
                    expected.SourceDirectory != observed.SourceDirectory ||
                    expected.Generator != observed.Generator ||
                    expected.MultiConfig != observed.MultiConfig ||
                    !expected.Configurations.SequenceEqual(observed.Configurations))
                {
                    throw new InvalidDataException($"Build tree changed since node '{last!.Id}'; resume refused.");
                }
            }

            foreach (NodeResult? node in saved.Nodes.Where(n => n.Status == "succeeded"))
            {
                if (node.Artifact is { } artifact)
                {
                    if (!File.Exists(artifact.Executable) ||
                        await CMakeWorkflowNodeExecutor.HashExecutableAsync(artifact.Executable, ct) != artifact.Sha256)
                    {
                        throw new InvalidDataException($"Artifact changed since node '{node.Id}'; resume refused.");
                    }
                }

                foreach (WorkflowArtifactEvidence evidence in node.DeclaredArtifacts)
                {
                    if (await WorkflowArtifacts.HashAsync(evidence.Path, ct) != evidence.Sha256
                        || (evidence.SnapshotPath is { } snapshot
                        && await WorkflowArtifacts.HashAsync(snapshot, ct) != evidence.Sha256))
                    {
                        throw new InvalidDataException(
                            $"Declared artifact changed since node '{node.Id}'; resume refused.");
                    }
                }
            }

            saved.ResumeCount++;
            saved.RecoveryPhase = "resuming";
            saved.Status = "running";
            saved.FinishedAt = null;
            saved.Error = null;
            saved.Findings.Clear();
            foreach (NodeResult? node in saved.Nodes.Where(n => n.Status != "succeeded"))
            {
                // A run can only be resumed after a pre-execution admission failure.
                // Downstream nodes were never launched.
                if (node.Status is not ("needs_attention" or "pending" or "blocked"))
                {
                    throw new InvalidDataException($"Unexpected non-resumable node '{node.Id}': {node.Status}.");
                }

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
            await Console.Error.WriteLineAsync($"[CMakeWorkflow] Environment provenance unavailable: {error.Message}");
        }
        catch (UnauthorizedAccessException error)
        {
            await Console.Error.WriteLineAsync($"[CMakeWorkflow] Environment provenance unavailable: {error.Message}");
        }

        Dictionary<string, NodeResult> states = report.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        Dictionary<string, BuildContextSession> sessions = plan.Nodes.Where(n => n.Definition.Kind != "profile_snapshot")
            .Select(n => n.Target.BuildDirectory).Distinct(WorkflowPlanner.PathComparer)
            .ToDictionary(p => p, _ => new BuildContextSession(), WorkflowPlanner.PathComparer);
        if (resume)
        {
            // Restore per-tree session admission state from the verified observations.
            foreach (PlannedNode? node in plan.Nodes.Where(n => states[n.Definition.Id].Status == "succeeded"))
            {
                NodeResult saved = states[node.Definition.Id];
                ObservedBuildContext? observed = saved.ObservationAfter ?? saved.ObservedContext;
                if (observed is null || node.Definition.Kind is "inspect" or "profile_snapshot")
                {
                    continue;
                }

                BuildContextSession session = sessions[node.Target.BuildDirectory];
                if (node.Definition.Kind == "configure")
                {
                    session.Configured(node.Requested, observed);
                }
                else
                {
                    _ = session.Observe(node.Requested, observed, "Resume");
                }
            }
        }

        BuildTreeOwnership? ownership = null;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using WorkflowEventJournal events = new(output, eventsToStdout, resume ? report : null);
        await SaveAsync(report, output, events);
        try
        {
            string[] mutationTrees = [.. plan.Nodes.Where(n => n.Definition.Kind is not ("inspect" or "profile_snapshot"))
                .Select(n => n.Target.BuildDirectory)];
            if (mutationTrees.Length != 0 && !resume)
            {
                ownership = await BuildTreeOwnership.AcquireAsync(mutationTrees, stop.Token);
            }

            // The engine owns ready/active admission; CMake owns persistence,
            // resource identity, node execution and authoritative report states.
            ExecutionPlan<PlannedNode> executionPlan = new(plan.Nodes.Select(node =>
                new ExecutionNode<PlannedNode>(node.Definition.Id, node.Dependencies,
                    node.Definition.Exclusive, node)).ToArray());
            await ExecutionScheduler.RunAsync(executionPlan, jobs, stop,
                new ExecutionSchedulerHooks<PlannedNode, NodeResult, bool>
                {
                    GetStatus = id => states[id].Status switch
                    {
                        "succeeded" => ExecutionStatus.Succeeded,
                        "failed" => ExecutionStatus.Failed,
                        "blocked" => ExecutionStatus.Blocked,
                        "canceled" => ExecutionStatus.Canceled,
                        "needs_attention" => ExecutionStatus.NeedsAttention,
                        "outcome_unknown" => ExecutionStatus.OutcomeUnknown,
                        "running" => ExecutionStatus.Running,
                        _ => ExecutionStatus.Pending
                    },
                    Conflicts = (left, right) => Conflicts(left.Payload, right.Payload),
                    MarkCanceled = item =>
                    {
                        NodeResult state = states[item.Id];
                        state.Status = "canceled";
                        state.Error = "Workflow canceled before this node started.";
                    },
                    MarkBlocked = item =>
                    {
                        NodeResult state = states[item.Id];
                        state.Status = "blocked";
                        state.Error = "A prerequisite did not succeed.";
                    },
                    BeforeReserve = (item, token) => RecoveryFaults.HitAsync("before-dispatch", item.Id, token),
                    Reserve = item =>
                    {
                        PlannedNode node = item.Payload;
                        NodeResult state = states[item.Id];
                        state.Status = "running";
                        state.StartedAt = DateTimeOffset.UtcNow;
                        string logStem = LogStem(report, item.Id);
                        state.Stdout = logStem + ".stdout.log";
                        state.Stderr = logStem + ".stderr.log";
                        Console.Error.WriteLine($"[CMakeWorkflow] START {item.Id}");
                        // Preserve the existing snapshot policy: only builds with
                        // a selected dependent run or profile hash the executable.
                        return node.Definition.Kind == "build" && plan.Nodes.Any(p =>
                            p.Definition.Kind is "run" or "profile" && p.Definition.From == item.Id);
                    },
                    PersistCheckpoint = () => SaveAsync(report, output, events),
                    ConfirmDispatch = beforeExternalDispatch is null
                        ? null : token => beforeExternalDispatch(output, token),
                    BeforeExecute = (item, token) => RecoveryFaults.HitAsync("after-checkpoint", item.Id, token),
                    Execute = (item, snapshot, token) =>
                    {
                        PlannedNode node = item.Payload;
                        Dictionary<string, NodeResult> sources = node.Definition.Inputs.Where(x => x.From is not null)
                            .Select(x => x.From!).Distinct(StringComparer.Ordinal)
                            .ToDictionary(id => id, id => states[id], StringComparer.Ordinal);
                        return CMakeWorkflowNodeExecutor.ExecuteAsync(node,
                            node.Definition.From is { } source ? states[source] : null,
                            snapshot, node.Definition.Kind == "profile_snapshot"
                                ? new BuildContextSession() : sessions[node.Target.BuildDirectory],
                            output, LogStem(report, item.Id), extension,
                            toolchains.GetValueOrDefault(node.Target.BuildDirectory), plan.File, sources, ct: token);
                    },
                    AfterExecute = (item, token) => RecoveryFaults.HitAsync("after-execution", item.Id, token),
                    Complete = (item, result) =>
                    {
                        states[item.Id] = result;
                        report.Nodes[report.Nodes.FindIndex(n => n.Id == item.Id)] = result;
                        if (result.Status == "needs_attention")
                        {
                            report.Findings.Add(new WorkflowFinding("TOOL_EXECUTABLE_MISSING", "error", item.Id,
                                result.Error ?? "Required executable is unavailable.",
                                result.Kind == "ctest" ? ["cmake"] : ["native"]));
                        }

                        Console.Error.WriteLine($"[CMakeWorkflow] {result.Status.ToUpperInvariant()} {item.Id}" +
                                                (result.Error is null ? "" : $": {result.Error}"));
                    },
                    DrainInFlight = (item, result) =>
                        report.Nodes[report.Nodes.FindIndex(n => n.Id == item.Id)] = result
                }, cancellation: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (NodeResult? node in report.Nodes.Where(n => n.Status == "pending"))
            {
                node.Status = "canceled";
            }
        }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
        catch (Exception error)
        {
            report.Error = error.Message;
            await Console.Error.WriteLineAsync($"[CMakeWorkflow] Workflow infrastructure failed: {error.Message}");
            foreach (NodeResult? node in report.Nodes.Where(n => n.Status == "pending"))
            {
                node.Status = "blocked";
                node.Error = "Workflow infrastructure failed: " + error.Message;
            }
        }
#pragma warning restore CA1031

        finally
        {
            // The engine drains admitted workers before this domain lease is released.
            // Cancel even if mutation-tree ownership failed before entering the engine.
            await stop.CancelAsync();
            ownership?.Dispose();
        }

        report.FinishedAt = DateTimeOffset.UtcNow;
        report.RecoveryPhase = resume ? "finished" : report.RecoveryPhase;
        // An admission whose persistence failed must never appear as safely
        // replayable. No new worker starts before SaveAsync succeeds.
        if (report.Nodes.Any(n => n.Status == "running"))
        {
            foreach (NodeResult? node in report.Nodes.Where(n => n.Status == "running"))
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
            report.BuildEnvironments = [.. report.Nodes.Where(n => n.Kind != "profile_snapshot")
                .Select(n => (n.BuildDirectory, n.Configuration))
                .Where(x => !string.IsNullOrWhiteSpace(x.BuildDirectory)).Distinct()
                .Select(x => EnvironmentProvenance.CaptureBuild(x.BuildDirectory, x.Configuration))
                .OfType<BuildEnvironment>()];
        }
        catch (IOException error)
        {
            await Console.Error.WriteLineAsync($"[CMakeWorkflow] Environment provenance unavailable: {error.Message}");
        }
        catch (UnauthorizedAccessException error)
        {
            await Console.Error.WriteLineAsync($"[CMakeWorkflow] Environment provenance unavailable: {error.Message}");
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
        int retries = report.Reconciliations.Count(r => r.NodeId == nodeId && r.Decision == "retry");
        return retries == 0 ? nodeId : $"{nodeId}.attempt-{retries + 1}";
    }

    private static async Task SaveAsync(WorkflowReport report, string output, WorkflowEventJournal events)
    {
        await WorkflowCheckpoint.SaveAsync(report, output, CancellationToken.None);
        events.Publish(report);
    }
}