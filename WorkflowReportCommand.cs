using System.Text.Json;

namespace CMakeWorkflow;

public static class WorkflowReportCommand
{
    public static async Task<int> ExecuteAsync(string runDirectory, CancellationToken ct,
        bool json = false)
    {
        try
        {
            var directory = Path.GetFullPath(runDirectory);
            var snapshot = await WorkflowQueries.ReportAsync(directory, ct);
            var report = snapshot.Report;
            if (json) Console.Out.WriteLine(JsonSerializer.Serialize(report, WorkflowJson.Default.WorkflowReport));
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
            Console.Error.WriteLine($"[CMakeWorkflow] Cannot read workflow report: {error.Message}");
            return 1;
        }
    }

    public static void Write(WorkflowReport report, string runDirectory)
    {
        Console.WriteLine($"Workflow: {report.Workflow}");
        Console.WriteLine($"Status: {report.Status.ToUpperInvariant()}");
        foreach (var decision in report.Reconciliations)
            Console.WriteLine($"Reconciliation: {decision.Timestamp:O} {decision.Actor}@{decision.Host} " +
                $"{decision.NodeId ?? "run"} -> {decision.Decision}; reason: {decision.Reason}; prior checkpoint: {decision.CheckpointSha256}");
        Console.WriteLine();
        Console.WriteLine("Nodes:");
        foreach (var node in report.Nodes)
        {
            var duration = node.DurationSeconds is { } seconds ? $"{seconds:F2}s" : "unknown";
            Console.WriteLine($"  {node.Id,-20} {node.Status.ToUpperInvariant(),-10} {duration}");
            if (node.ExitCode is { } exitCode)
                Console.WriteLine($"    exit code: {exitCode}");
            if (node.BuildVerification is { } build)
                Console.WriteLine($"    native build verification: {build.Verification} ({build.Target.Target}, {build.Target.Configuration ?? "default"})");
            if (node.Artifact is { } artifact)
                Console.WriteLine($"    artifact: {artifact.Executable} [{artifact.Materialization}]");
            if (node.ExperimentResult != "NotRun")
                Console.WriteLine($"    experiment result: {node.ExperimentResult}");
            if (node.Stdout is { } stdout)
                Console.WriteLine($"    stdout: {Path.GetFullPath(Path.Combine(runDirectory, stdout))}");
            if (node.Stderr is { } stderr)
                Console.WriteLine($"    stderr: {Path.GetFullPath(Path.Combine(runDirectory, stderr))}");
            if (!string.IsNullOrWhiteSpace(node.Error))
                Console.WriteLine($"    diagnostic: {node.Error}");
        }

        var contexts = report.Nodes.Where(n => n.RequestedContext is not null)
            .GroupBy(n => n.BuildDirectory, WorkflowPlanner.PathComparer);
        foreach (var context in contexts)
        {
            var node = context.Last();
            var state = node.SessionAfter ?? node.SessionBefore;
            var observed = node.ObservationAfter ?? node.ObservedContext;
            Console.WriteLine();
            Console.WriteLine($"Context: {context.Key}");
            Console.WriteLine($"  Mode: {node.RequestedContext!.Mode ?? "Unknown"}");
            Console.WriteLine($"  Provenance: {state?.Provenance ?? observed?.PresetState ?? "Unknown"}");
            Console.WriteLine($"  Freshness: Unverified (native incremental decisions belong to CMake/backend)");
            if (observed is null || observed.State is "Unknown" or "Unverified")
                Console.WriteLine($"  Observation: {observed?.State ?? "Unknown"}");
            foreach (var diagnostic in observed?.Diagnostics ?? [])
                Console.WriteLine($"  Unverified/unknown: {diagnostic}");
        }
        if (!string.IsNullOrWhiteSpace(report.Error))
        {
            Console.WriteLine();
            Console.WriteLine($"Workflow diagnostic: {report.Error}");
        }
    }
}
