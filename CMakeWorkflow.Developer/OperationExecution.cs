using System.Text;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Workflows;

namespace CMakeWorkflow.Developer;

public static partial class DeveloperLifecycle
{
    /// <summary>Reevaluates the approved plan and publishes its exact immutable bytes in the project root.</summary>
    public static async Task<string> PublishOperationManifestAsync(DeveloperOperationRequest request, string manifestSha256,
        CancellationToken ct = default)
    {
        DeveloperOperationPlan current = await PlanOperationAsync(request, ct);
        if (current.ManifestSha256 != manifestSha256)
        {
            throw new InvalidDataException("The operation changed after planning. Review the new plan.");
        }

        string destination = BuildVariantConfiguration.CheckedPath(current.ProjectDirectory, current.ManifestName);
        if (!File.Exists(destination))
        {
            string temporary = Path.Combine(current.ProjectDirectory, ".cmwf-developer-staging-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(current.ManifestJson, ct);
                    await stream.FlushAsync(ct);
                }
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporary, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    // Another publisher won. Verify the bytes before using the path.
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        string checkedPath = BuildVariantConfiguration.CheckedPath(current.ProjectDirectory, current.ManifestName);
        await using FileStream manifest = File.OpenRead(checkedPath);
        return manifest.Length == current.ManifestJson.Length &&
            Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(manifest, ct)) == manifestSha256
            ? checkedPath : throw new InvalidDataException("The immutable developer manifest was modified: " + checkedPath);
    }

    /// <summary>Publishes locally and admits that manifest through a client connected to the local project workspace.</summary>
    public static async Task<WorkflowOperation> ExecuteOperationAsync(CMakeWorkflowClient client, DeveloperExecutionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        DiscoveredProject project = await DiscoverProjectAsync(request.Operation.Location, ct);
        if (!string.Equals(project.Directory, Path.TrimEndingDirectorySeparator(client.Host.WorkspaceDirectory),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The local client workspace must be the discovered project root. Use explicit staged Host paths for remote execution.");
        }

        if (string.IsNullOrWhiteSpace(request.ClientRequestId) || request.ClientRequestId.Length > 128 || request.Jobs < 1)
        {
            throw new ArgumentException("A request identity of 1–128 characters and a positive job count are required.", nameof(request));
        }

        string resultsRoot = Path.GetFullPath(request.ResultsDirectory ?? "out/results", project.Directory);
        string resultsDirectory = Path.Combine(resultsRoot, "developer-" +
            BuildVariantConfiguration.Hash(Encoding.UTF8.GetBytes(request.ClientRequestId)));
        DeveloperOperationRequest pinned = request.Operation with { Location = new(project.Directory) { ProjectDirectory = project.Directory } };
        string manifest = await PublishOperationManifestAsync(pinned, request.ManifestSha256, ct);
        WorkflowPreflightResult preflight = await client.Workflows.PreflightAsync(
            new WorkflowPreflightOptions(manifest, request.ManifestSha256, request.Jobs, resultsDirectory)
            {
                HistoryPolicy = request.HistoryPolicy
            }, ct);
        if (!preflight.Valid)
        {
            IEnumerable<string> errors = preflight.Nodes.SelectMany(node => node.Diagnostics
                .Where(diagnostic => diagnostic.Severity == "Error").Select(diagnostic => node.Id + ": " + diagnostic.Message));
            throw new InvalidDataException("Host preflight rejected the workflow. " + string.Join(" | ", errors));
        }
        return await client.Workflows.StartAsync(preflight.CreateStartOptions(request.ClientRequestId), ct);
    }

    /// <summary>Admits an already staged manifest on a Host. Host paths are explicit and are sent unchanged.</summary>
    public static Task<WorkflowOperation> ExecuteStagedOperationAsync(CMakeWorkflowClient client, DeveloperOperationPlan plan,
        StagedDeveloperExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(execution);
        return client.Workflows.StartAsync(new StartWorkflowOptions(execution.ClientRequestId, execution.ManifestPath,
            plan.ManifestSha256, execution.Jobs, execution.ResultsDirectory)
        {
            HistoryPolicy = execution.HistoryPolicy,
            SourceSnapshotId = execution.SourceSnapshotId
        }, ct);
    }
}
