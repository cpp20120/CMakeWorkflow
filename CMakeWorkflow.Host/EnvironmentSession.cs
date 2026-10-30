using CMakeWorkflow.Tooling;

namespace CMakeWorkflow.Host;

public sealed partial class DoctorSession
{
    private string GetEnvironmentRequirementsPath(string path)
    {
        if (workspace is null) throw new InvalidOperationException("Call initialize first.");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Requirements path must be absolute.");
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(workspace, full);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Requirements must be within the workspace.");
        if (!File.Exists(full)) throw new FileNotFoundException("Requirements not found.", full);
        // Avoid opening attacker-controlled symlinks escaping the workspace.
        for (var current = new FileInfo(full).Directory; current is not null &&
             current.FullName.Length >= workspace.Length; current = current.Parent)
        {
            if (current.LinkTarget is not null)
                throw new ArgumentException("Symlinked requirements directories are forbidden.");
        }
        if (new FileInfo(full).LinkTarget is not null)
            throw new ArgumentException("Symlinked requirements files are forbidden.");
        return full;
    }

    public async Task<HostPreparationPlan> EnvironmentPlanAsync(EnvironmentPlanRequest request,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disconnected);
        await gate.WaitAsync(lifetime.Token);
        try
        {
            return await HostPreparation.InspectAsync(GetEnvironmentRequirementsPath(request.RequirementsPath),
                createTooling(workspace!), PackageManagerKind.Auto, null, lifetime.Token);
        }
        finally { gate.Release(); }
    }

    public async Task<HostPreparationResult> EnvironmentApplyAsync(EnvironmentApplyRequest request,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disconnected);
        await gate.WaitAsync(lifetime.Token);
        try
        {
            var path = GetEnvironmentRequirementsPath(request.RequirementsPath);
            using var fixLease = fixes.TryAcquire() ?? throw new InvalidOperationException("Provisioning busy.");
            var service = createTooling(workspace!);
            var plan = await HostPreparation.InspectAsync(path, service, PackageManagerKind.Auto, null, lifetime.Token);
            if (string.IsNullOrEmpty(request.ApprovalSha256) ||
                !string.Equals(request.ApprovalSha256, plan.ApprovalSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Plan changed; review environment/plan again.");
            return await HostPreparation.ApplyAsync(plan, service, PackageManagerKind.Auto, null,
                lifetime.Token, request.AllowSystemChanges, auditDirectory: workspace);
        }
        finally { gate.Release(); }
    }
}
