using System.Runtime.InteropServices;
using CMakeWorkflow.CMake.Toolchains;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Toolchains;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    /// <summary>Inspects Linux experiment capacity without staging or admitting a workflow.</summary>
    public Task<ExperimentHostResources> GetExperimentResourcesAsync(ExperimentResourceRequest request,
        CancellationToken ct) => ReadAsync(request, async (_, token) =>
    {
        if (!OperatingSystem.IsLinux() || (request.ManagedClang && RuntimeInformation.ProcessArchitecture != Architecture.X64))
        {
            throw new ApplicationRefusal("unsupported_platform", "The requested experiment profile is unavailable on this Host.");
        }

        WorkflowToolchainRuntime? runtime = request.ManagedClang
            ? await new WorkflowToolchainProvider().ResolveAsync(
                new(EnvironmentProfileId: "linux-managed-clang", Generator: "Ninja Multi-Config"), token)
            : null;
        return new ExperimentHostResources(RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount, runtime?.Linux?.CompilerVersion, runtime?.Fingerprint);
    }, ct);
}
