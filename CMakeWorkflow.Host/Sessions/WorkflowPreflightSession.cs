using System.Security.Cryptography;

namespace CMakeWorkflow.Host;

public sealed partial class DoctorSession
{
    public Task<WorkflowPreflightResult> WorkflowPreflightAsync(WorkflowPreflightRequest request,
        CancellationToken cancellationToken) => ReadAsync(request, async (paths, ct) =>
    {
        if (request.Jobs < 1)
            throw new ApplicationRefusal("invalid_argument", "jobs must be positive.");
        if (request.ManifestSha256 is not { Length: 64 } || !request.ManifestSha256.All(Uri.IsHexDigit))
            throw new ApplicationRefusal("invalid_argument", "ManifestSha256 must be a SHA-256 hex digest.");
        var manifest = paths.Require(request.Manifest);
        var results = paths.Require(request.ResultsDirectory);
        paths.Read(manifest);
        var bytes = await File.ReadAllBytesAsync(manifest, ct);
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(digest, request.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new ApplicationRefusal("manifest_changed", "Manifest bytes differ from the expected SHA-256.");

        // The planner consumes the verified bytes instead of reopening the file.
        // Read guards on referenced inputs/contexts are identical to workflow/plan.
        var plan = WorkflowQueries.PlanView(await WorkflowPlanner.LoadSnapshotAsync(
            manifest, bytes, [], null, ct, paths.Read));
        return new WorkflowPreflightResult(1, manifest, digest, request.Jobs, results, plan.Valid,
            plan.Nodes.Select(node => new WorkflowPreflightNode(node.Id, node.Kind, node.Target,
                node.BuildDirectory, node.Configuration, node.Dependencies.ToList(),
                node.Resources.ToList(), node.Exclusive, node.ObservedState,
                node.Diagnostics.Select(d => new WorkflowPreflightDiagnostic(d.Severity, d.Message)).ToList()))
                .ToList());
    }, cancellationToken);
}
