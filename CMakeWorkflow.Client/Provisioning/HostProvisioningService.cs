#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

/// <summary>Host-side tool inspection and explicit, single-use fix-plan execution.</summary>
public sealed class HostProvisioningService(CMakeWorkflowClient client)
{
    /// <summary>Read-only tool inspection. Does not install packages.</summary>
    public Task<HostToolingPlan> DoctorAsync(HostDoctorOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        client.RequireCapability("tooling/doctor");
        return RpcRefusals.TranslateAsync(() => client.Proxy.DoctorAsync(options, ct));
    }

    /// <summary>
    /// Produces an expiring, connection-bound fix plan. No install occurs until ApplyFixAsync.
    /// Losing the connection invalidates the plan; call PlanFixAsync again after reconnect.
    /// </summary>
    public Task<HostFixPlan> PlanFixAsync(HostPlanFixOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        client.RequireCapability("tooling/planFix");
        return RpcRefusals.TranslateAsync(() => client.Proxy.PlanFixAsync(options, ct));
    }

    /// <summary>
    /// Explicitly authorize one attempt at the reviewed plan. This is a mutating operation;
    /// cancellation or transport loss can leave partial installation. Never auto-retry.
    /// </summary>
    public Task<HostFixResult> ApplyFixAsync(HostApplyFixOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        client.RequireCapability("tooling/applyFix");
        return RpcRefusals.TranslateAsync(() => client.Proxy.ApplyFixAsync(options, ct));
    }
}
