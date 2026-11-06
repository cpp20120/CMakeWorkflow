using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Serialization;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Commands;
#pragma warning restore IDE0130

[SuppressMessage("Maintainability", "CA1515", Justification = "Preserve the established public CLI entry points used by integrations.")]
public static class WorkflowReportCommand
{
    public static async Task<int> ExecuteAsync(string runDirectory, CancellationToken ct,
        bool json = false)
    {
        try
        {
            string directory = Path.GetFullPath(runDirectory);
            WorkflowReportSnapshot snapshot = await WorkflowQueries.ReportAsync(directory, ct);
            WorkflowReport report = snapshot.Report;
            if (json)
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(report, WorkflowJson.Default.WorkflowReport));
            }
            else
            {
                Console.WriteLine($"Checkpoint SHA256: {snapshot.Revision}");
                Write(report, directory);
            }

            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or JsonException or InvalidDataException or ArgumentException)
        {
            await Console.Error.WriteLineAsync($"[CMakeWorkflow] Cannot read workflow report: {error.Message}");
            return 1;
        }
    }

    public static void Write(WorkflowReport report, string runDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        Console.WriteLine($"Workflow: {report.Workflow}");
        Console.WriteLine($"Status: {report.Status.ToUpperInvariant()}");
        foreach (WorkflowReconciliation decision in report.Reconciliations)
        {
            Console.WriteLine($"Reconciliation: {decision.Timestamp:O} {decision.Actor}@{decision.Host} " +
                              $"{decision.NodeId ?? "run"} -> {decision.Decision}; reason: {decision.Reason}; prior checkpoint: {decision.CheckpointSha256}");
        }

        Console.WriteLine();
        Console.WriteLine("Nodes:");
        foreach (NodeResult node in report.Nodes)
        {
            string duration = node.DurationSeconds is { } seconds ? $"{seconds:F2}s" : "unknown";
            Console.WriteLine($"  {node.Id,-20} {node.Status.ToUpperInvariant(),-10} {duration}");
            if (node.ExitCode is { } exitCode)
            {
                Console.WriteLine($"    exit code: {exitCode}");
            }

            if (node.BuildVerification is { } build)
            {
                Console.WriteLine(
                    $"    native build verification: {build.Verification} ({build.Target.Target}, {build.Target.Configuration ?? "default"})");
            }

            if (node.Artifact is { } artifact)
            {
                Console.WriteLine($"    artifact: {artifact.Executable} [{artifact.Materialization}]");
            }

            if (node.ExperimentResult != "NotRun")
            {
                Console.WriteLine($"    experiment result: {node.ExperimentResult}");
            }

            if (node.DeclaredArtifacts.Count != 0)
            {
                Console.WriteLine(
                    $"    declared artifacts: {node.DeclaredArtifacts.Count} verified records (see run.json)");
            }

            if (node.ExecutionConditions.TryGetValue("cpuSetRequested", out string? cpus))
            {
                Console.WriteLine($"    required CPU set: {cpus} (taskset admission)");
            }

            if (node.ExecutionConditions.TryGetValue("profilerBackend", out string? backend))
            {
                Console.WriteLine($"    profiler backend: {backend}");
            }
            else if (node.ExecutionConditions.TryGetValue("profilerRequired", out string? required))
            {
                Console.WriteLine($"    required profiler: {required} (capture not confirmed)");
            }

            if (node.Stdout is { } stdout)
            {
                Console.WriteLine($"    stdout: {Path.GetFullPath(Path.Combine(runDirectory, stdout))}");
            }

            if (node.Stderr is { } stderr)
            {
                Console.WriteLine($"    stderr: {Path.GetFullPath(Path.Combine(runDirectory, stderr))}");
            }

            if (!string.IsNullOrWhiteSpace(node.Error))
            {
                Console.WriteLine($"    diagnostic: {node.Error}");
            }
        }

        IEnumerable<IGrouping<string, NodeResult>> contexts = report.Nodes.Where(n => n.RequestedContext is not null)
            .GroupBy(n => n.BuildDirectory, WorkflowPlanner.PathComparer);
        foreach (IGrouping<string, NodeResult> context in contexts)
        {
            NodeResult node = context.Last();
            BuildContextSessionState? state = node.SessionAfter ?? node.SessionBefore;
            ObservedBuildContext? observed = node.ObservationAfter ?? node.ObservedContext;
            Console.WriteLine();
            Console.WriteLine($"Context: {context.Key}");
            Console.WriteLine($"  Mode: {node.RequestedContext!.Mode ?? "Unknown"}");
            Console.WriteLine($"  Provenance: {state?.Provenance ?? observed?.PresetState ?? "Unknown"}");
            Console.WriteLine($"  Freshness: Unverified (native incremental decisions belong to CMake/backend)");
            if (observed is null || observed.State is "Unknown" or "Unverified")
            {
                Console.WriteLine($"  Observation: {observed?.State ?? "Unknown"}");
            }

            foreach (string diagnostic in observed?.Diagnostics ?? [])
            {
                Console.WriteLine($"  Unverified/unknown: {diagnostic}");
            }
        }

        if (!string.IsNullOrWhiteSpace(report.Error))
        {
            Console.WriteLine();
            Console.WriteLine($"Workflow diagnostic: {report.Error}");
        }
    }
}