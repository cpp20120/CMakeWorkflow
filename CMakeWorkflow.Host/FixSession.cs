using System.Text.Json;
using CMakeWorkflow.Tooling;

namespace CMakeWorkflow.Host;

public sealed partial class DoctorSession
{
    private sealed record PendingFix(string Id, DateTimeOffset ExpiresAt, PlanFixRequest Request, string Snapshot, string? RunSnapshot);
    private PendingFix? pendingFix;

    public async Task<FixPlan> PlanFixAsync(PlanFixRequest request, CancellationToken cancellationToken)
    {
        RequireWorkspace();
        ValidateProfiles(request.Profiles, request.InstallDirectory);
        var boundRun = request.RunDirectory is null ? null : await RecoveryRunBinding.ReadAsync(
            workspace!, request.RunDirectory, request.Profiles, cancellationToken);
        if (!Enum.IsDefined(request.PackageManager)) throw new ArgumentException("Unknown package manager.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disconnected);
        await gate.WaitAsync(lifetime.Token);
        try
        {
            pendingFix = null;
            var plan = await createTooling(workspace!).PlanAsync(request.Profiles, request.PackageManager,
                request.AllowFallback, request.InstallDirectory, lifetime.Token);
            var expires = (timeProvider ?? TimeProvider.System).GetUtcNow().AddMinutes(10);
            var id = Guid.NewGuid().ToString("N");
            pendingFix = new(id, expires, request with { Profiles = request.Profiles.ToArray() }, Snapshot(plan), boundRun?.Snapshot);
            return new(1, id, expires, plan, boundRun?.Directory, boundRun?.Snapshot);
        }
        finally { gate.Release(); }
    }

    public async Task<FixResult> ApplyFixAsync(ApplyFixRequest request, CancellationToken cancellationToken)
    {
        RequireWorkspace();
        ValidateOperation(request.OperationId);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disconnected);
        await gate.WaitAsync(lifetime.Token);
        try
        {
            var saved = pendingFix;
            if (saved is null || saved.Id != request.PlanId || saved.ExpiresAt <= (timeProvider ?? TimeProvider.System).GetUtcNow())
                throw new ArgumentException("Fix plan is unknown, expired or already consumed. Review a new plan on this connection.");
            pendingFix = null; // single use, including failed/canceled attempts
            var sequence = 0;
            async Task Notify(string stage, string? tool = null, string? message = null)
            {
                if (disconnected.IsCancellationRequested) return;
                try { await fixProgress(new(request.OperationId, ++sequence, stage, tool, message)); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { /* Reporting failure must not trigger another installation step or retry. */ }
            }
            await Notify("started");
            FixResult result;
            using var lease = fixes.TryAcquire();
            if (lease is null)
                result = new(1, "busy", null, null, ["Another Host Fix may be running, or its lock is unavailable. No installation was attempted. Review a new plan later."]);
            else
            {
                // Hold WorkflowRunner's run lease through installation and re-check.
                using var runLease = saved.Request.RunDirectory is null ? null :
                    TryAcquireRunLease(saved.Request.RunDirectory);
                if (saved.Request.RunDirectory is not null && runLease is null)
                    result = new(1, "run_busy", null, null,
                        ["The suspended run is active. No installation was attempted."]);
                else
                {
                // The reviewed run must still be the same suspended execution.
                var runUnchanged = saved.RunSnapshot is null || await RecoveryRunBinding.MatchesAsync(
                    workspace!, saved.Request.RunDirectory!, saved.Request.Profiles, saved.RunSnapshot, lifetime.Token);
                if (!runUnchanged)
                {
                    result = new(1, "run_changed", null, null,
                        ["Suspended run changed after review. No installation was attempted. Review a new fix plan."]);
                }
                else
                {
                var service = createTooling(workspace!);
                var current = await service.PlanAsync(saved.Request.Profiles, saved.Request.PackageManager,
                    saved.Request.AllowFallback, saved.Request.InstallDirectory, lifetime.Token);
                if (Snapshot(current) != saved.Snapshot)
                    result = new(1, "plan_changed", null, current, ["Tools or installation steps changed after review. No installation was attempted. Review the new plan."]);
                else
                {
                    ToolingResult? installation = null;
                    var diagnostics = new List<string>();
                    var canceled = false;
                    try
                    {
                        installation = await service.ExecuteAsync(current, lifetime.Token,
                            p => Notify(p.Stage, p.Tool, p.Message));
                        diagnostics.AddRange(installation.Diagnostics);
                    }
                    catch (OperationCanceledException)
                    { canceled = true; diagnostics.Add("Fix was canceled or disconnected. Installation may be partial; no rollback or automatic retry was performed."); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    { diagnostics.Add("Installation failed: " + error.Message); }
                    await Notify("verifying");
                    ToolingPlan? verification = null;
                    // Re-probe even after failure/cancel, without restarting installation.
                    using var verifyTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    try
                    {
                        verification = await service.PlanAsync(saved.Request.Profiles, saved.Request.PackageManager,
                            saved.Request.AllowFallback, saved.Request.InstallDirectory, verifyTimeout.Token);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    { diagnostics.Add("Post-install verification unavailable: " + error.Message); }
                    if (verification is { Ready: true })
                        diagnostics.Add("Tools are available to Doctor. Use installation directory '" + verification.InstallDirectory
                            + "' in the execution environment if it is not already on PATH; existing CMake trees retain their recorded toolchain.");
                    var ready = !canceled && installation?.Success == true && verification?.Ready == true
                        && (saved.RunSnapshot is null || await RecoveryRunBinding.MatchesAsync(
                            workspace!, saved.Request.RunDirectory!, saved.Request.Profiles, saved.RunSnapshot,
                            CancellationToken.None));
                    result = new(1, canceled ? "canceled" : ready ? "succeeded" : "failed",
                        installation, verification, diagnostics.ToArray(), ready && saved.RunSnapshot is not null);
                }
                }
                }
            }
            await Notify("finished", message: result.Status);
            return result;
        }
        finally { gate.Release(); }
    }

    private static FileStream? TryAcquireRunLease(string runDirectory)
    {
        try
        {
            return new FileStream(Path.Combine(runDirectory, ".workflow-run.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string Snapshot(ToolingPlan plan) => JsonSerializer.Serialize(plan, HostJson.Default.ToolingPlan);
    private void RequireWorkspace()
    { if (workspace is null) throw new InvalidOperationException("Call initialize first."); }
    private static void ValidateOperation(string id)
    { if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("Invalid operation ID."); }
    private static void ValidateProfiles(string[] profiles, string? directory)
    {
        if (profiles is null || profiles.Length > 32 || profiles.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Invalid tool profiles.");
        if (directory is not null && !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("installDirectory must be absolute.");
    }
}
