using System.Diagnostics;
using System.Text.Json;
using CMakeWorkflow;
using CMakeWorkflow.Engine;

namespace CMakeWorkflow.Host;

// Actual non-CMake domain consumer of ExecutionScheduler, DurableByteWriter
// and WorkflowProcessBackend. Only this adapter knows the process manifest and
// report; the neutral scheduler never writes domain state.
internal static class ProcessWorkflowRunner
{
    internal static async Task<int> RunAsync(ProcessWorkflowManifest manifest, string snapshotSha256,
        WorkspaceReadPaths paths, string output, int jobs, CancellationToken cancellationToken,
        Func<Task> confirmDispatch)
    {
        Directory.CreateDirectory(output);
        await using var runLease = new FileStream(Path.Combine(output, ".workflow-run.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var report = new ProcessOperationReport
        {
            SnapshotSha256 = snapshotSha256,
            Nodes = manifest.Nodes.Select(n => new ProcessNodeReport { Id = n.Id }).ToList()
        };
        var states = report.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var index = manifest.Nodes.Select((node, i) => (node.Id, i))
            .ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var graph = new ExecutionPlan<ProcessWorkflowNode>(manifest.Nodes.Select(n =>
            new ExecutionNode<ProcessWorkflowNode>(n.Id, n.Dependencies ?? [], n.Exclusive, n)).ToList());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var journal = new ProcessEventJournal(output);

        async Task Persist()
        {
            // A write-ahead safety commit must not be interrupted by a client
            // cancellation after dispatch intent has entered the report.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ProcessWorkflowJson.Default.ProcessOperationReport);
            await DurableByteWriter.WriteAsync(Path.Combine(output, "run.json"), bytes, CancellationToken.None);
            journal.Publish(report);
        }
        await Persist();
        try
        {
            await ExecutionScheduler.RunAsync(graph, jobs, cancellationToken, stop,
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
                    BeforeReserve = (_, _) => Task.CompletedTask,
                    Reserve = node =>
                    {
                        var state = states[node.Id];
                        state.Status = "running";
                        state.StartedAt = DateTimeOffset.UtcNow;
                        var stem = "process_" + index[node.Id].ToString("D4");
                        state.Stdout = stem + ".stdout.log";
                        state.Stderr = stem + ".stderr.log";
                        return index[node.Id];
                    },
                    PersistCheckpoint = Persist,
                    ConfirmDispatch = _ => confirmDispatch(),
                    BeforeExecute = (_, _) => Task.CompletedTask,
                    Execute = (node, reservation, token) => ExecuteNodeAsync(node.Payload,
                        states[node.Id], paths, output, token, cancellationToken),
                    AfterExecute = (_, _) => Task.CompletedTask,
                    Complete = (node, completed) => Apply(states[node.Id], completed),
                    DrainInFlight = (node, completed) => Apply(states[node.Id], completed)
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var state in report.Nodes.Where(x => x.Status == "pending")) state.Status = "canceled";
        }
        catch (Exception error)
        {
            report.Error = error.Message;
            foreach (var state in report.Nodes.Where(x => x.Status == "pending"))
            {
                state.Status = "blocked";
                state.Error = "Execution infrastructure failed: " + error.Message;
            }
        }
        report.FinishedAt = DateTimeOffset.UtcNow;
        if (report.Nodes.Any(x => x.Status == "running"))
        {
            foreach (var state in report.Nodes.Where(x => x.Status == "running"))
                state.Status = "outcome_unknown";
            report.Status = "outcome_unknown";
        }
        else if (cancellationToken.IsCancellationRequested) report.Status = "canceled";
        else if (report.Error is null && report.Nodes.All(x => x.Status == "succeeded"))
            report.Status = "succeeded";
        else report.Status = "failed";
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

    private static async Task<ProcessNodeReport> ExecuteNodeAsync(ProcessWorkflowNode node,
        ProcessNodeReport state, WorkspaceReadPaths paths, string output,
        CancellationToken stop, CancellationToken requestCancellation)
    {
        var result = new ProcessNodeReport { Id = node.Id, Status = "running" };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (node.TimeoutSeconds is { } seconds)
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            var wd = node.WorkingDirectory is null ? paths.WorkspaceDirectory
                : Path.IsPathFullyQualified(node.WorkingDirectory)
                    ? node.WorkingDirectory : Path.Combine(paths.WorkspaceDirectory, node.WorkingDirectory);
            wd = paths.Require(wd);
            var info = new ProcessStartInfo(node.Executable)
            {
                UseShellExecute = false, WorkingDirectory = wd,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in node.Arguments ?? []) info.ArgumentList.Add(arg);
            if (node.Environment is not null)
                foreach (var (key, value) in node.Environment) info.Environment[key] = value;
            await using var stdout = File.Create(Path.Combine(output, state.Stdout!));
            await using var stderr = File.Create(Path.Combine(output, state.Stderr!));
            deadline.Token.ThrowIfCancellationRequested();
            var execution = await WorkflowProcessBackend.RunAsync(new(info, stdout, stderr),
                deadline.Token, null, _ => { });
            result.ExitCode = execution.ExitCode;
            result.Status = execution.ExitCode == 0 && execution.StartError is null ? "succeeded" : "failed";
            if (execution.StartError is not null) result.Error = execution.StartError.Message;
            else if (execution.ExitCode != 0) result.Error = "Process exited with code " + execution.ExitCode;
        }
        catch (OperationCanceledException)
        {
            result.Status = requestCancellation.IsCancellationRequested ? "canceled" : "failed";
            result.Error = requestCancellation.IsCancellationRequested ? "Canceled." :
                $"Timed out after {node.TimeoutSeconds} seconds.";
        }
        catch (Exception error)
        {
            result.Status = "failed";
            result.Error = error.Message;
        }
        result.FinishedAt = DateTimeOffset.UtcNow;
        return result;
    }
}
