using CMakeWorkflow.Client;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Scripting;

namespace CMakeWorkflow.Developer;

/// <summary>Read-only engineering plans and explicit delivery of their exact reviewed bytes.</summary>
public static class ReviewedEngineeringWorkflow
{
    /// <summary>Rechecks the snapshotted selection and captures the exact reviewed bytes for Scripting.</summary>
    public static async Task<WorkflowManifest> MaterializeAsync(DeveloperOperationRequest request,
        DeveloperOperationPlan reviewed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reviewed);
        byte[] bytes = reviewed.ManifestJson.ToArray();
        WorkflowManifest manifest = new(reviewed.Kind.ToString(), bytes);
        if (manifest.Sha256 != reviewed.ManifestSha256)
        {
            throw new InvalidDataException("The reviewed manifest bytes do not match their digest.");
        }

        DeveloperOperationPlan current = await DeveloperLifecycle.ReviewOperationAgainAsync(request, reviewed, ct);
        return current.ManifestSha256 == reviewed.ManifestSha256
            ? manifest : throw new InvalidDataException("Engineering evidence changed. Review the new operation plan.");
    }

    /// <summary>Delivers the reviewed manifest without staging source or changing tool/evidence state.</summary>
    public static async Task<PreparedWorkflow> PrepareAsync(CMakeWorkflowClient client, DeveloperOperationRequest request,
        DeveloperOperationPlan reviewed, string resultsDirectory, string manifestBase = ".", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        WorkflowManifest manifest = await MaterializeAsync(request, reviewed, ct);
        return await manifest.PrepareAsync(client.Workflows, resultsDirectory, manifestBase, ct);
    }

    /// <summary>Observes Host preflight for the prepared immutable manifest and selected History policy.</summary>
    public static Task<WorkflowPreflightResult> PreflightAsync(CMakeWorkflowClient client, PreparedWorkflow prepared,
        HistoryPolicy? history = null, int jobs = 1, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(prepared);
        return client.Workflows.PreflightAsync(new WorkflowPreflightOptions(
            prepared.Manifest, prepared.ManifestSha256, jobs, prepared.ResultsDirectory)
        {
            HistoryPolicy = history
        }, ct);
    }
}
