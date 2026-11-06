using CMakeWorkflow.Host.Provisioning;
using CMakeWorkflow.Host.Toolchains;
using CMakeWorkflow.Tooling.Environment;
using CMakeWorkflow.Tooling.Toolchains;
using CMakeWorkflow.Tooling.Tools;
using PolyType;
using StreamJsonRpc;

namespace CMakeWorkflow.Host.Protocol;

[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IHostRpc
{
    [JsonRpcMethod("initialize")]
    Task<HostInfo> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("tooling/doctor")]
    Task<ToolingPlan> DoctorAsync(DoctorRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("tooling/planFix")]
    Task<FixPlan> PlanFixAsync(PlanFixRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("toolchains/discover")]
    Task<ToolchainInventory> ToolchainsDiscoverAsync(ToolchainDiscoverRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("toolchains/resolve")]
    Task<ToolchainResolution> ToolchainsResolveAsync(ToolchainResolveRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("toolchains/inspectEnvironment")]
    Task<DeveloperEnvironmentSnapshot> ToolchainsInspectEnvironmentAsync(ToolchainInspectEnvironmentRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("environment/plan")]
    Task<HostPreparationPlan> EnvironmentPlanAsync(EnvironmentPlanRequest request, CancellationToken cancellationToken);

    [JsonRpcMethod("environment/apply")]
    Task<HostPreparationResult> EnvironmentApplyAsync(EnvironmentApplyRequest request,
        CancellationToken cancellationToken);

    [JsonRpcMethod("tooling/applyFix")]
    Task<FixResult> ApplyFixAsync(ApplyFixRequest request, CancellationToken cancellationToken);
}
