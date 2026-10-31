using CMakeWorkflow.Tooling;

namespace CMakeWorkflow.Host;

// One instance per connection. Never changes process-wide cwd or environment.
public sealed partial class DoctorSession(Func<string, ToolingService> createTooling,
    Func<DoctorProgress, Task> progress, CancellationToken disconnected,
    FixCoordinator fixes, Func<FixProgress, Task> fixProgress, TimeProvider? timeProvider = null,
    DurableExecutionCoordinator? durableExecutions = null) : IHostRpc
{
    private string? workspace;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task DrainAsync()
    {
        await gate.WaitAsync();
        gate.Release();
    }

    public Task<HostInfo> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ProtocolVersion != 1)
            throw new ArgumentException($"Unsupported Host protocol {request.ProtocolVersion}; expected 1.");
        if (string.IsNullOrWhiteSpace(request.WorkspaceDirectory)
            || !Path.IsPathFullyQualified(request.WorkspaceDirectory)
            || !Directory.Exists(request.WorkspaceDirectory))
            throw new ArgumentException("workspaceDirectory must be an existing absolute directory.");
        var directory = Path.GetFullPath(request.WorkspaceDirectory);
        var previous = Interlocked.CompareExchange(ref workspace, directory, null);
        if (previous is not null && !WorkflowPlanner.PathComparer.Equals(
                BuildTreeOwnership.CanonicalPath(previous), BuildTreeOwnership.CanonicalPath(directory)))
            throw new InvalidOperationException("Reconnect to select a different workspace.");
        directory = previous ?? directory;
        return Task.FromResult(new HostInfo(1, Environment.ProcessId, directory,
            ["tooling/doctor", "tooling/progress", "tooling/planFix", "tooling/applyFix", "tooling/fixProgress", "environment/plan", "environment/apply", "request-cancellation", .. ReadCapabilities],
            ToolCatalog.AllProfiles.Select(p => new ToolingProfileView(p.Id, p.Description, p.Tools)).ToList()));
    }

    public async Task<ToolingPlan> DoctorAsync(DoctorRequest request, CancellationToken cancellationToken)
    {
        if (workspace is null) throw new InvalidOperationException("Call initialize first.");
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128
            || request.Profiles is null || request.Profiles.Length > 32
            || request.Profiles.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Invalid Doctor request.");
        if (request.InstallDirectory is not null && !Path.IsPathFullyQualified(request.InstallDirectory))
            throw new ArgumentException("installDirectory must be absolute.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disconnected);
        // Bound each connection to one probe at a time; separate clients remain independent.
        await gate.WaitAsync(lifetime.Token);
        try
        {
            var sequence = 0;
            await progress(new(request.OperationId, ++sequence, "started", null));
            var plan = await createTooling(workspace).PlanAsync(request.Profiles, PackageManagerKind.Auto, true,
                request.InstallDirectory, lifetime.Token,
                tool => progress(new(request.OperationId, ++sequence, "tool_checked", tool)));
            await progress(new(request.OperationId, ++sequence, "finished", null));
            return plan;
        }
        finally { gate.Release(); }
    }
}
