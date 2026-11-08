using System.Diagnostics.CodeAnalysis;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Provisioning;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Host.Sessions;

// One instance per connection. Never changes process-wide cwd or environment.
[method: SuppressMessage("Design", "CA1068", Justification = "disconnected is the connection lifetime captured by the session, not a per-call cancellation token; the constructor also requires Fix coordination and notification callbacks.")]
public sealed partial class DoctorSession(
    Func<string, ToolingService> createTooling,
    Func<DoctorProgress, Task> progress,
    CancellationToken disconnected,
    FixCoordinator fixes,
    Func<FixProgress, Task> fixProgress,
    TimeProvider? timeProvider = null,
    DurableExecutionCoordinator? durableExecutions = null) : IHostRpc, IDisposable
{
    private string? workspace;
    private readonly SemaphoreSlim gate = new(1, 1);

    // Call only after the 32-digit hex request-ID guard. This representation is
    // stable for on-disk preparation binding names across platforms.
    private static string CanonicalRequestId(string value) =>
        Convert.ToHexStringLower(Convert.FromHexString(value));

    public async Task DrainAsync()
    {
        await gate.WaitAsync();
        _ = gate.Release();
    }

    // The connection owner calls DrainAsync after RPC shutdown before disposing
    // the per-session gate. Durable operations are owned by the Host coordinator.
    public void Dispose()
    {
        gate.Dispose();
        GC.SuppressFinalize(this);
    }

    public Task<HostInfo> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ProtocolVersion != 1)
        {
            throw new ArgumentException($"Unsupported Host protocol {request.ProtocolVersion}; expected 1.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkspaceDirectory)
            || !Path.IsPathFullyQualified(request.WorkspaceDirectory)
            || !Directory.Exists(request.WorkspaceDirectory))
        {
            throw new ArgumentException("workspaceDirectory must be an existing absolute directory.");
        }

        string directory = Path.GetFullPath(request.WorkspaceDirectory);
        string? previous = Interlocked.CompareExchange(ref workspace, directory, null);
        if (previous is not null && !WorkflowPlanner.PathComparer.Equals(
                BuildTreeOwnership.CanonicalPath(previous), BuildTreeOwnership.CanonicalPath(directory)))
        {
            throw new InvalidOperationException("Reconnect to select a different workspace.");
        }

        directory = previous ?? directory;
        return Task.FromResult(new HostInfo(1, Environment.ProcessId, directory,
            [
                "tooling/doctor", "tooling/progress", "tooling/planFix", "tooling/applyFix", "tooling/fixProgress",
                "environment/plan", "environment/apply", "request-cancellation", .. ReadCapabilities
            ],
            [.. ToolCatalog.AllProfiles.Select(p => new ToolingProfileView(p.Id, p.Description, p.Tools))]));
    }

    public async Task<ToolingPlan> DoctorAsync(DoctorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (workspace is null)
        {
            throw new InvalidOperationException("Call initialize first.");
        }

        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128
                                                           || request.Profiles is null || request.Profiles.Length > 32
                                                           || request.Profiles.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Invalid Doctor request.");
        }

        if (request.InstallDirectory is not null && !Path.IsPathFullyQualified(request.InstallDirectory))
        {
            throw new ArgumentException("installDirectory must be absolute.");
        }

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disconnected);
        // Bound each connection to one probe at a time; separate clients remain independent.
        await gate.WaitAsync(lifetime.Token);
        try
        {
            int sequence = 0;
            await progress(new(request.OperationId, ++sequence, "started", null));
            ToolingPlan plan = await createTooling(workspace).PlanAsync(request.Profiles, PackageManagerKind.Auto, true,
                request.InstallDirectory, lifetime.Token,
                tool => progress(new(request.OperationId, ++sequence, "tool_checked", tool)));
            await progress(new(request.OperationId, ++sequence, "finished", null));
            return plan;
        }
        finally
        {
            _ = gate.Release();
        }
    }
}