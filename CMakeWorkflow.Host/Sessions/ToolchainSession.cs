using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Toolchains;
using CMakeWorkflow.Tooling.Toolchains;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    // Read-only observations: never initialize a developer environment in the
    // Host process. IDs must be freshly resolved server-side, not trusted paths.
    public Task<ToolchainInventory> ToolchainsDiscoverAsync(ToolchainDiscoverRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, async (_, ct) => await WindowsToolchains.DiscoverAsync(ct), cancellationToken);

    public Task<ToolchainResolution> ToolchainsResolveAsync(ToolchainResolveRequest request,
        CancellationToken cancellationToken) =>
        ReadAsync(request, async (_, ct) =>
        {
            ArgumentNullException.ThrowIfNull(request.Selection);
            ToolchainInventory inventory = await WindowsToolchains.DiscoverAsync(ct);
            return WindowsToolchains.Resolve(inventory, request.Selection);
        }, cancellationToken);

    public Task<DeveloperEnvironmentSnapshot> ToolchainsInspectEnvironmentAsync(
        ToolchainInspectEnvironmentRequest request, CancellationToken cancellationToken) =>
        ReadAsync(request, async (_, ct) =>
        {
            ArgumentNullException.ThrowIfNull(request.Environment);
            if (!OperatingSystem.IsWindows())
            {
                throw new ApplicationRefusal("unsupported_platform", "Developer Command Prompt requires Windows.");
            }

            ToolchainInventory inventory = await WindowsToolchains.DiscoverAsync(ct);
            return await WindowsToolchains.InspectEnvironmentAsync(inventory, request.Environment, ct);
        }, cancellationToken);
}