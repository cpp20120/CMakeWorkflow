using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;

namespace CMakeWorkflow.CMake.Reporting;

// Shared by CLI formatting and Host RPC. These methods do not execute processes,
// acquire execution leases, create queries/results, or change recovery state.
public static class WorkflowQueries
{
    public static WorkflowPlanView PlanView(WorkflowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(1, plan.File, plan.ManifestHash,
                !plan.Nodes.Any(n => n.Diagnostics.Any(d => d.Severity == "Error")),
                [.. plan.Nodes.Select(n => new WorkflowPlanNodeView(n.Definition.Id, n.Definition.Kind,
            n.Target.Target, n.Target.BuildDirectory, n.Target.Configuration, [.. n.Dependencies],
            n.Observed.State, [.. n.Diagnostics], [.. n.Definition.Resources], n.Definition.Exclusive,
            [.. n.Definition.Inputs], [.. n.Definition.Outputs], n.Definition.Constraints))]);
    }
    public static async Task<WorkflowReportSnapshot> ReportAsync(string directory, CancellationToken ct,
        Action<string>? readGuard = null)
    {
        directory = Path.GetFullPath(directory);
        string path = Path.Combine(directory, "run.json");
        readGuard?.Invoke(path);
        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        return ReportFromBytes(directory, bytes);
    }

    // Allows the Host's secure, handle-relative artifact reader to use the same
    // validation as CLI reports without reopening a checked pathname.
    public static WorkflowReportSnapshot ReportFromBytes(string directory, byte[] bytes)
    {
        WorkflowReport report = JsonSerializer.Deserialize(bytes, Serialization.WorkflowJson.Default.WorkflowReport)
                     ?? throw new InvalidDataException("run.json contains an empty report.");
        return report.SchemaVersion != 1
            ? throw new InvalidDataException($"Unsupported report schemaVersion: {report.SchemaVersion}.")
            : report.Nodes is null || string.IsNullOrWhiteSpace(report.Workflow) ||
            string.IsNullOrWhiteSpace(report.Status)
            || report.Nodes.Any(n =>
                n is null || string.IsNullOrWhiteSpace(n.Id) || string.IsNullOrWhiteSpace(n.Status))
            || report.Nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != report.Nodes.Count
            ? throw new InvalidDataException("run.json is missing workflow identity, status or valid node entries.")
            : new(1, directory, Convert.ToHexString(SHA256.HashData(bytes)), report);
    }

    public static async Task<TargetResolutionView> ResolveAsync(string buildDirectory, string target,
        string? configuration, CancellationToken ct, Action<string>? readGuard = null)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("Target is required.");
        }

        BuiltArtifact artifact =
            await CMakeFileApi.ResolveExecutableAsync(new(buildDirectory, target, configuration), ct, readGuard);
        return new(1, artifact.TargetRef.BuildDirectory, artifact.TargetRef.Target, artifact.TargetRef.Configuration,
            artifact.ExecutablePath);
    }
}