using CMakeWorkflow.Host.Provisioning;
using CMakeWorkflow.Tooling.Tools;
using CMakeWorkflow.Tooling.VisualStudio;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    private VisualStudioSetupService CreateVisualStudioService() =>
        createVisualStudio?.Invoke() ?? new(new SystemVisualStudioSetupBackend());

    private async Task<FixPlan> PlanVisualStudioFixAsync(PlanFixRequest request, CancellationToken cancellationToken)
    {
        VisualStudioSetupRequest visualStudio = request.VisualStudio
            ?? throw new ArgumentException("Missing Visual Studio setup request.");
        if (request.Profiles is not { Length: 0 } || request.InstallDirectory is not null ||
            request.RunDirectory is not null || request.PackageManager != PackageManagerKind.Auto ||
            !request.AllowFallback)
        {
            throw new ArgumentException(
                "Visual Studio Fix cannot be combined with ordinary tooling profiles, installation options, or run binding.");
        }

        if (visualStudio.Profiles is null || visualStudio.Profiles.Count > 32 ||
            visualStudio.Profiles.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Invalid Visual Studio profiles.");
        }

        if (!OperatingSystem.IsWindows() && createVisualStudio is null)
        {
            throw new PlatformNotSupportedException("Visual Studio provisioning requires Windows.");
        }

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, disconnected);
        await gate.WaitAsync(lifetime.Token);
        try
        {
            pendingFix = null;
            VisualStudioSetupPlan plan = await CreateVisualStudioService().PlanAsync(visualStudio, lifetime.Token);
            DateTimeOffset expires = (timeProvider ?? TimeProvider.System).GetUtcNow().AddMinutes(10);
            string id = Guid.NewGuid().ToString("N");
            VisualStudioSetupRequest captured = visualStudio with { Profiles = [.. visualStudio.Profiles] };
            pendingFix = new(id, expires, request with { Profiles = [], VisualStudio = captured },
                plan.ApprovalSha256, null);
            return new(1, id, expires, null, VisualStudioPlan: plan);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    private async Task<FixResult> ApplyVisualStudioFixAsync(PendingFix saved, ApplyFixRequest request,
        CancellationToken ct)
    {
        int sequence = 0;
        async Task Notify(string stage, string? message = null)
        {
            if (disconnected.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await fixProgress(new(request.OperationId, ++sequence, stage, "visual-studio", message));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Notifications must never cause a second installation attempt.
            }
        }

        await Notify("started");
        FixResult result;
        using (IDisposable? lease = fixes.TryAcquire())
        {
            if (lease is null)
            {
                result = new(1, "busy", null, null,
                    ["Another Host Fix is running. No installation was attempted."]);
            }
            else
            {
                VisualStudioSetupRequest options = saved.Request.VisualStudio!;
                VisualStudioSetupService service = CreateVisualStudioService();
                List<string> diagnostics = [];
                bool canceled = false;
                VisualStudioSetupPlan? current = null;
                try
                {
                    current = await service.PlanAsync(options, ct);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    diagnostics.Add("Plan revalidation was canceled. No installation was attempted.");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    diagnostics.Add("Plan revalidation failed: " + error.Message);
                }

                if (current is null)
                {
                    result = new(1, canceled ? "canceled" : "failed", null, null, [.. diagnostics]);
                }
                else if (!current.ApprovalSha256.Equals(saved.Snapshot, StringComparison.OrdinalIgnoreCase))
                {
                    result = new(1, "plan_changed", null, null,
                        ["Visual Studio components or setup arguments changed after review. No installation was attempted."],
                        VisualStudioVerification: current);
                }
                else
                {
                    VisualStudioSetupResult? installation = null;
                    try
                    {
                        await Notify("installing");
                        installation = await service.ApplyAsync(options, saved.Snapshot, ct);
                        if (installation.Diagnostic is not null)
                        {
                            diagnostics.Add(installation.Diagnostic);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        canceled = true;
                        diagnostics.Add("Installation canceled or disconnected. Partial changes may remain; no automatic retry.");
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        diagnostics.Add("Visual Studio installation failed: " + error.Message);
                    }

                    await Notify("verifying");
                    VisualStudioSetupPlan? verification = null;
                    using CancellationTokenSource verificationDeadline = new(TimeSpan.FromMinutes(2));
                    try
                    {
                        verification = await service.PlanAsync(options, verificationDeadline.Token);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        diagnostics.Add("Post-install verification unavailable: " + error.Message);
                    }

                    bool ready = !canceled && installation?.Ready == true && verification?.Ready == true;
                    result = new(1, canceled ? "canceled" : ready ? "succeeded" : "failed",
                        null, null, [.. diagnostics],
                        VisualStudioInstallation: installation, VisualStudioVerification: verification);
                }
            }
        }

        await Notify("finished", result.Status);
        return result;
    }
}
