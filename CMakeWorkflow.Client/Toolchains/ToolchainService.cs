#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

public sealed class ToolchainService(CMakeWorkflowClient client)
{
    public Task<ToolchainInventory> DiscoverAsync(CancellationToken ct = default)
    {
        client.RequireCapability("toolchains/discover");
        return client.Proxy.ToolchainsDiscoverAsync(new(), ct);
    }

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