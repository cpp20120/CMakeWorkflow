using System.Diagnostics;
using System.Text.Json;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.Core.Execution;
using CMakeWorkflow.Core.Persistence;
using CMakeWorkflow.Core.Processes;
using CMakeWorkflow.Host.Sessions;

namespace CMakeWorkflow.Host.Execution;

// Actual non-CMake domain consumer of ExecutionScheduler, DurableByteWriter
// and WorkflowProcessBackend. Only this adapter knows the process manifest and
// report; the neutral scheduler never writes domain state.
/// <summary>Adapts the neutral scheduler to process-domain reports, logs and child execution.</summary>
/// <remarks>
/// Caller owns durable acceptance, results reservation and one-way dispatch authorization. This runner owns the
/// exclusive run lease, mutable report and journal for the duration of execution. It does not finalize ledger state.
/// Checkpoints are authoritative; the event journal is a best-effort projection. No external process starts before
/// the complete running-intent batch is durably saved and the caller confirmation callback succeeds.
/// </remarks>
internal static class ProcessWorkflowRunner
{
    /// <summary>Runs an authorized process graph, or one explicitly admitted recovery attempt.</summary>
    /// <remarks>
    /// Requires a validated graph and positive jobs. Saves safety checkpoints with CancellationToken.None, even when
    /// execution is canceled; cancellation does not undo persisted intent. A failed persistence/gate prevents its batch dispatch.
    /// Resume rechecks exact checkpoint bytes under the run lease and permanently claims before changing report state.
    /// Only nodes validated as never admitted are reset; successful results and their original logs are retained.
    /// Scheduler exceptions can be projected into report.Error; remaining running nodes become outcome_unknown.
    /// Final checkpoint failure can still throw, so a returned code and ledger finalization remain separate boundaries.
    /// </remarks>
    /// <param name="manifest">Validated immutable process graph.</param>
    /// <param name="snapshotSha256">Accepted snapshot hash to embed in every checkpoint.</param>
    /// <param name="paths">Authorized workspace policy for run and working-directory paths.</param>
    /// <param name="output">Host run directory, exclusively leased here; must exist for resume.</param>
    /// <param name="jobs">Positive maximum concurrently admitted nodes.</param>
    /// <param name="confirmDispatch">Caller gate binding the newly persisted running checkpoint before any process in the batch starts.</param>
    /// <param name="resume">Whether to recover an existing, explicitly admitted run.</param>
    /// <param name="expectedCheckpointSha256">Required exact checkpoint hash for resume; unused for a new run.</param>
    /// <param name="claimResume">Permanent attempt claim callback required for resume; invoked under the run lease before report mutation.</param>
    /// <param name="cancellationToken">Host execution lifetime token; not the RPC token that submitted durable work.</param>
    /// <returns>0 for success, 1 for failure, 130 for cancellation, or 4 for unresolved outcome; persistence failures may throw.</returns>
    internal static async Task<int> RunAsync(ProcessWorkflowManifest manifest, string snapshotSha256,
        WorkspaceReadPaths paths, string output, int jobs, Func<Task> confirmDispatch, bool resume = false, string? expectedCheckpointSha256 = null,
        Func<Task>? claimResume = null, CancellationToken cancellationToken = default)
    {
        if (resume && !Directory.Exists(output))
        {
            throw new DirectoryNotFoundException(output);
        }

        _ = paths.Require(output);
        _ = Directory.CreateDirectory(output);
        await using FileStream runLease = new(paths.Require(Path.Combine(output, ".workflow-run.lock")),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        ProcessOperationReport report;
        if (resume)
        {
            string file = paths.Require(Path.Combine(output, "run.json"));
            paths.Read(file);
            byte[] bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            if (ProcessWorkflowRecovery.Hash(bytes) != expectedCheckpointSha256)
            {
                throw new InvalidOperationException("resume_checkpoint_changed");
            }

            _ = ProcessCheckpointEvidenceCodec.Instance.ForResume(bytes);
            report = ProcessCheckpointEvidenceCodec.Parse(bytes);
            ProcessWorkflowRecovery.ValidatePlan(report, manifest, snapshotSha256, paths, output);
            await RecoveryFaults.HitAsync("process-before-resume-claim", null, cancellationToken);
            if (claimResume is null)
            {
                throw new InvalidOperationException("resume_claim_required");
            }

            await claimResume();
            await RecoveryFaults.HitAsync("process-after-resume-claim", null, cancellationToken);
            report.ResumeCount++;
            report.RecoveryPhase = "resuming";
            report.Status = "running";
            report.Error = null;
            report.FinishedAt = null;
            foreach (ProcessNodeReport? node in report.Nodes.Where(ProcessCheckpointEvidenceCodec.Eligible))
            {
                node.Status = "pending";
                node.Error = null;
            }
        }
        else
        {
            report = new ProcessOperationReport
            {
                SnapshotSha256 = snapshotSha256,
                Nodes = [.. manifest.Nodes.Select(n => new ProcessNodeReport { Id = n.Id, Dependencies = n.Dependencies ?? [] })]
            };
        }

        Dictionary<string, ProcessNodeReport> states = report.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        Dictionary<string, int> index = manifest.Nodes.Select((node, i) => (node.Id, i))
            .ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        ExecutionPlan<ProcessWorkflowNode> graph = new(manifest.Nodes
            .Where(n => states[n.Id].Status is not ("failed" or "canceled")).Select(n =>
            new ExecutionNode<ProcessWorkflowNode>(n.Id, n.Dependencies ?? [], n.Exclusive, n)).ToList());
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = paths.Require(Path.Combine(output, "events.jsonl"));
        using ProcessEventJournal journal = new(output, resume);

        async Task Persist()
        {
            // A write-ahead safety commit must not be interrupted by a client
            // cancellation after dispatch intent has entered the report.
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, ProcessWorkflowJson.Default.ProcessOperationReport);
            string checkpoint = paths.Require(Path.Combine(output, "run.json"));
            _ = paths.Require(checkpoint + ".tmp");
            await DurableByteWriter.WriteAsync(checkpoint, bytes, CancellationToken.None);
            journal.Publish(report);
        }
        await Persist();
        try
        {
            await ExecutionScheduler.RunAsync(graph, jobs, stop,
                new ExecutionSchedulerHooks<ProcessWorkflowNode, ProcessNodeReport, int>
                {
                    GetStatus = id => states[id].Status switch
                    {
                        "succeeded" => ExecutionStatus.Succeeded,
                        "failed" => ExecutionStatus.Failed,
                        "blocked" => ExecutionStatus.Blocked,
                        "canceled" => ExecutionStatus.Canceled,
                        "needs_attention" => ExecutionStatus.NeedsAttention,
                        "running" => ExecutionStatus.Running,
                        "outcome_unknown" => ExecutionStatus.OutcomeUnknown,
                        _ => ExecutionStatus.Pending
                    },
                    Conflicts = (a, b) => a.Exclusive || b.Exclusive ||
                        (a.Payload.Resources ?? []).Intersect(b.Payload.Resources ?? [],
                            StringComparer.Ordinal).Any(),
                    MarkCanceled = node => states[node.Id].Status = "canceled",
                    MarkBlocked = node => states[node.Id].Status = "blocked",
                    BeforeReserve = (node, token) => RecoveryFaults.HitAsync("process-before-reserve", node.Id, token),
                    Reserve = node =>
                    {
                        ProcessNodeReport state = states[node.Id];
                        state.Status = "running";
                        state.StartedAt = DateTimeOffset.UtcNow;
                        string stem = "process_" + index[node.Id].ToString("D4", System.Globalization.CultureInfo.CurrentCulture) +
                            (resume ? ".resume-" + report.ResumeCount : "");
                        state.Stdout = stem + ".stdout.log";
                        state.Stderr = stem + ".stderr.log";
                        return index[node.Id];
                    },
                    PersistCheckpoint = Persist,
                    ConfirmDispatch = async token =>
                    {
                        await RecoveryFaults.HitAsync("process-checkpoint-before-confirm", null, token);
                        await confirmDispatch();
                        await RecoveryFaults.HitAsync("process-checkpoint-confirmed", null, token);
                    },
                    BeforeExecute = (node, token) => RecoveryFaults.HitAsync("process-before-spawn", node.Id, token),
                    Execute = (node, reservation, token) => ExecuteNodeAsync(node.Payload,
                        states[node.Id], paths, output, token, cancellationToken),
                    AfterExecute = (node, token) => RecoveryFaults.HitAsync("process-child-completed", node.Id, token),
                    Complete = (node, completed) => Apply(states[node.Id], completed),
                    DrainInFlight = (node, completed) => Apply(states[node.Id], completed)
                }, cancellation: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (ProcessNodeReport? state in report.Nodes.Where(x => x.Status == "pending"))
            {
                state.Status = "canceled";
            }
        }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
        catch (Exception error)
        {
            report.Error = error.Message;
            foreach (ProcessNodeReport? state in report.Nodes.Where(x => x.Status == "pending"))
            {
                state.Status = "blocked";
                state.Error = "Execution infrastructure failed: " + error.Message;
            }
        }
#pragma warning restore CA1031

        report.FinishedAt = DateTimeOffset.UtcNow;
        if (report.Nodes.Any(x => x.Status == "running"))
        {
            foreach (ProcessNodeReport? state in report.Nodes.Where(x => x.Status == "running"))
            {
                state.Status = "outcome_unknown";
            }

            report.Status = "outcome_unknown";
        }
        else
        {
            report.Status = cancellationToken.IsCancellationRequested
                ? "canceled"
                : report.Error is null && report.Nodes.All(x => x.Status == "succeeded") ? "succeeded" : "failed";
        }

        report.RecoveryPhase = report.Status == "outcome_unknown" ? "interrupted" : "finished";
        await RecoveryFaults.HitAsync("process-before-terminal-checkpoint", null, cancellationToken);
        await Persist();
        return report.Status switch { "succeeded" => 0, "canceled" => 130, "outcome_unknown" => 4, _ => 1 };
    }

    private static void Apply(ProcessNodeReport state, ProcessNodeReport result)
    {
        state.Status = result.Status;
        state.Error = result.Error;
        state.ExitCode = result.ExitCode;
        state.FinishedAt = result.FinishedAt;
    }

    /// <summary>Executes one admitted process with new log files and a node deadline.</summary>
    /// <remarks>
    /// Owns log destinations until the process backend returns or throws; the backend owns child/pump cleanup.
    /// Opening logs can occur before the pre-start cancellation check. Cancellation is projected as canceled only
    /// when the outer execution token is canceled; other cancellation is classified as failure. Other exceptions
    /// become failed node diagnostics. Such projection is not independent proof that orphan descendants have stopped.
    /// </remarks>
    /// <param name="node">Validated immutable launch definition.</param>
    /// <param name="state">Reserved report state supplying unique log names; not mutated by this worker.</param>
    /// <param name="paths">Workspace authorization policy.</param>
    /// <param name="output">Run directory for create-new log destinations.</param>
    /// <param name="stop">Scheduler stop token, linked to the per-node timeout.</param>
    /// <param name="requestCancellation">Outer Host execution token used to classify cancellation; despite the name, durable callers do not pass an RPC token.</param>
    private static async Task<ProcessNodeReport> ExecuteNodeAsync(ProcessWorkflowNode node,
        ProcessNodeReport state, WorkspaceReadPaths paths, string output,
        CancellationToken stop, CancellationToken requestCancellation)
    {
        ProcessNodeReport result = new() { Id = node.Id, Status = "running" };
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (node.TimeoutSeconds is { } seconds)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        try
        {
            string wd = node.WorkingDirectory is null ? paths.WorkspaceDirectory
                : Path.IsPathFullyQualified(node.WorkingDirectory)
                    ? node.WorkingDirectory : Path.Combine(paths.WorkspaceDirectory, node.WorkingDirectory);
            wd = paths.Require(wd);
            ProcessStartInfo info = new(node.Executable)
            {
                UseShellExecute = false,
                WorkingDirectory = wd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string arg in node.Arguments ?? [])
            {
                info.ArgumentList.Add(arg);
            }

            if (node.Environment is not null)
            {
                foreach ((string? key, string? value) in node.Environment)
                {
                    info.Environment[key] = value;
                }
            }

            await using FileStream stdout = new(paths.Require(Path.Combine(output, state.Stdout!)), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using FileStream stderr = new(paths.Require(Path.Combine(output, state.Stderr!)), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            deadline.Token.ThrowIfCancellationRequested();
            WorkflowProcessBackend.Outcome execution = await WorkflowProcessBackend.RunAsync(new(info, stdout, stderr),
null, _ => { }, ct: deadline.Token);
            result.ExitCode = execution.ExitCode;
            result.Status = execution.ExitCode == 0 && execution.StartError is null ? "succeeded" : "failed";
            if (execution.StartError is not null)
            {
                result.Error = execution.StartError.Message;
            }
            else if (execution.ExitCode != 0)
            {
                result.Error = "Process exited with code " + execution.ExitCode;
            }
        }
        catch (OperationCanceledException)
        {
            result.Status = requestCancellation.IsCancellationRequested ? "canceled" : "failed";
            result.Error = requestCancellation.IsCancellationRequested ? "Canceled." :
                $"Timed out after {node.TimeoutSeconds} seconds.";
        }

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
        catch (Exception error)
        {
            result.Status = "failed";
            result.Error = error.Message;
        }
#pragma warning restore CA1031

        result.FinishedAt = DateTimeOffset.UtcNow;
        return result;
    }
}
