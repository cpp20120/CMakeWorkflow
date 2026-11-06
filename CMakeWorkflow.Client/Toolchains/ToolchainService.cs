namespace CMakeWorkflow.Client.Toolchains;

/// <summary>Host-local toolchain inspection facade borrowing the client lifetime.</summary>
/// <remarks>
/// Queries do not establish future tool availability or reserve candidates. Workflow admission resolves IDs afresh.
/// Cancellation affects the query only; the SDK does not reconnect or retry automatically.
/// </remarks>
public sealed class ToolchainService(CMakeWorkflowClient client)
{
    /// <summary>Discovers the current Host toolchain inventory without binding workflow execution.</summary>
    public Task<ToolchainInventory> DiscoverAsync(CancellationToken ct = default)
    {
        client.RequireCapability("toolchains/discover");
        return client.Proxy.ToolchainsDiscoverAsync(new(), ct);
    }

    /// <summary>Resolves candidate identities on the Host without configuring or building a workflow.</summary>
    public Task<ToolchainResolution> ResolveAsync(ToolchainSelection selection, CancellationToken ct = default)
    {
        client.RequireCapability("toolchains/resolve");
        return client.Proxy.ToolchainsResolveAsync(new(selection), ct);
    }

    /// <summary>Preview only; does not modify the Host or affect workflow execution.</summary>
    public Task<DeveloperEnvironmentSnapshot> InspectEnvironmentAsync(DeveloperEnvironmentRequest request,
        CancellationToken ct = default)
    {
        client.RequireCapability("toolchains/inspectEnvironment");
        return client.Proxy.ToolchainsInspectEnvironmentAsync(new(request), ct);
    }
}