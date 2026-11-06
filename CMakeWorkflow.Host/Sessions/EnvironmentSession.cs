using CMakeWorkflow.Host.Provisioning;
using CMakeWorkflow.Tooling.Environment;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    private string GetEnvironmentRequirementsPath(string path)
    {
        if (workspace is null)
        {
            throw new InvalidOperationException("Call initialize first.");
        }

        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Requirements path must be absolute.");
        }

        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(workspace, full);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Requirements must be within the workspace.");
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException("Requirements not found.", full);
        }
        // Avoid opening attacker-controlled symlinks escaping the workspace.
        for (DirectoryInfo? current = new FileInfo(full).Directory;
             current is not null &&
             current.FullName.Length >= workspace.Length;
             current = current.Parent)
        {
            if (current.LinkTarget is not null)
            {
                throw new ArgumentException("Symlinked requirements directories are forbidden.");
            }
        }

        return new FileInfo(full).LinkTarget is not null ? throw new ArgumentException("Symlinked requirements files are forbidden.") : full;
    }

    public async Task<HostPreparationPlan> EnvironmentPlanAsync(EnvironmentPlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disconnected);
        await gate.WaitAsync(lifetime.Token);
        try
        {
            return await HostPreparation.InspectAsync(GetEnvironmentRequirementsPath(request.RequirementsPath),
                createTooling(workspace!), PackageManagerKind.Auto, null, lifetime.Token);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    public async Task<HostPreparationResult> EnvironmentApplyAsync(EnvironmentApplyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disconnected);
        await gate.WaitAsync(lifetime.Token);
        try
        {
            string path = GetEnvironmentRequirementsPath(request.RequirementsPath);
            using IDisposable fixLease = fixes.TryAcquire() ?? throw new InvalidOperationException("Provisioning busy.");
            ToolingService service = createTooling(workspace!);
            HostPreparationPlan plan = await HostPreparation.InspectAsync(path, service, PackageManagerKind.Auto, null, lifetime.Token);
            return string.IsNullOrEmpty(request.ApprovalSha256) ||
                !string.Equals(request.ApprovalSha256, plan.ApprovalSha256, StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidOperationException("Plan changed; review environment/plan again.")
                : await HostPreparation.ApplyAsync(plan, service, PackageManagerKind.Auto, null,
                lifetime.Token, request.AllowSystemChanges, auditDirectory: workspace);
        }
        finally
        {
            _ = gate.Release();
        }
    }
}