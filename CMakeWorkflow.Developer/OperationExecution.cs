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
        return await PublishReviewedManifestAsync(current, manifestSha256, ct);
    }

    /// <summary>Publishes a reviewed plan using its snapshotted selection.</summary>
    public static async Task<string> PublishOperationManifestAsync(DeveloperOperationRequest request, DeveloperOperationPlan reviewed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reviewed);
        if (request.Configuration.Kit is { } explicitKit && explicitKit != reviewed.SelectedKit?.Id)
        {
            throw new InvalidDataException("The explicit Kit differs from the reviewed plan.");
        }
        DeveloperOperationPlan current = reviewed.KitMatrix.Count != 0 ? await PlanKitMatrixAsync(request, ct) :
            await PlanOperationCoreAsync(request with
            {
                Configuration = request.Configuration with { Kit = reviewed.SelectedKit?.Id }
            }, useActive: false, ct);
        return WorkflowPathEquals(current.ProjectDirectory, reviewed.ProjectDirectory)
            ? await PublishReviewedManifestAsync(current, reviewed.ManifestSha256, ct)
            : throw new InvalidDataException("The reviewed operation belongs to a different project.");
    }

    private static bool WorkflowPathEquals(string first, string second) =>
        string.Equals(first, second, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<string> PublishReviewedManifestAsync(DeveloperOperationPlan current, string manifestSha256, CancellationToken ct)
    {
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
        if (request.ReviewedPlan is { } reviewed && reviewed.ManifestSha256 != request.ManifestSha256)
        {
            throw new InvalidDataException("The reviewed plan does not match the requested manifest identity.");
        }
        string manifest = request.ReviewedPlan is { } plan ? await PublishOperationManifestAsync(pinned, plan, ct) :
            await PublishOperationManifestAsync(pinned, request.ManifestSha256, ct);
        return await StartReviewedAsync(client, new(request.ClientRequestId, manifest,
            request.ManifestSha256, request.Jobs, resultsDirectory)
        {
            HistoryPolicy = request.HistoryPolicy
        }, ct);
    }

    private static async Task<WorkflowOperation> StartReviewedAsync(CMakeWorkflowClient client, StartWorkflowOptions start, CancellationToken ct)
    {
        WorkflowPreflightResult preflight = await client.Workflows.PreflightAsync(
            new WorkflowPreflightOptions(start.Manifest, start.ManifestSha256, start.Jobs, start.ResultsDirectory)
            {
                HistoryPolicy = start.HistoryPolicy
            }, ct);
        if (!preflight.Valid)
        {
            IEnumerable<string> errors = preflight.Nodes.SelectMany(node => node.Diagnostics
                .Where(diagnostic => diagnostic.Severity == "Error").Select(diagnostic => node.Id + ": " + diagnostic.Message));
            throw new InvalidDataException("Host preflight rejected the workflow. " + string.Join(" | ", errors));
        }
        return await client.Workflows.StartAsync(preflight.CreateStartOptions(start.ClientRequestId), ct);
    }

    /// <summary>Admits an already staged manifest on a Host. Host paths are explicit and are sent unchanged.</summary>
    public static Task<WorkflowOperation> ExecuteStagedOperationAsync(CMakeWorkflowClient client, DeveloperOperationPlan plan,
        StagedDeveloperExecution execution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(execution);
        StartWorkflowOptions start = new(execution.ClientRequestId, execution.ManifestPath,
            plan.ManifestSha256, execution.Jobs, execution.ResultsDirectory)
        {
            HistoryPolicy = execution.HistoryPolicy,
            SourceSnapshotId = execution.SourceSnapshotId
        };
        return (plan.KitLockSha256 is not null || plan.KitMatrix.Count != 0) && execution.SourceSnapshotId is null ? StartReviewedAsync(client, start, ct) :
            client.Workflows.StartAsync(start, ct);
    }
}
