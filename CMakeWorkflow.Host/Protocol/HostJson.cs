using CMakeWorkflow.Contracts.History;
using System.Text.Json.Serialization;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.Contracts.Recipes;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.History;
using CMakeWorkflow.History.Analysis;
using CMakeWorkflow.History.Models;
using CMakeWorkflow.Host.Campaigns;
using CMakeWorkflow.Host.History;
using CMakeWorkflow.Host.Profiling;
using CMakeWorkflow.Host.Provisioning;
using CMakeWorkflow.Host.Recipes;
using CMakeWorkflow.Host.Toolchains;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Tooling.Environment;
using CMakeWorkflow.Tooling.Toolchains;
using CMakeWorkflow.Tooling.Tools;
using CMakeWorkflow.Tooling.VisualStudio;

namespace CMakeWorkflow.Host.Protocol;

[JsonSerializable(typeof(string[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters =
    [
        typeof(JsonStringEnumConverter<ToolPlatform>),
        typeof(JsonStringEnumConverter<PackageManagerKind>)
    ])]
[JsonSerializable(typeof(InitializeRequest))]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(DoctorRequest))]
[JsonSerializable(typeof(DoctorProgress))]
[JsonSerializable(typeof(ToolingPlan))]
[JsonSerializable(typeof(PlanFixRequest))]
[JsonSerializable(typeof(FixPlan))]
[JsonSerializable(typeof(ApplyFixRequest))]
[JsonSerializable(typeof(FixProgress))]
[JsonSerializable(typeof(FixResult))]
[JsonSerializable(typeof(VisualStudioSetupRequest))]
[JsonSerializable(typeof(VisualStudioSetupPlan))]
[JsonSerializable(typeof(VisualStudioSetupResult))]
[JsonSerializable(typeof(ToolchainDiscoverRequest))]
[JsonSerializable(typeof(ToolchainResolveRequest))]
[JsonSerializable(typeof(ToolchainInspectEnvironmentRequest))]
[JsonSerializable(typeof(ToolchainInventory))]
[JsonSerializable(typeof(ToolchainSelection))]
[JsonSerializable(typeof(WorkflowToolchainBinding))]
[JsonSerializable(typeof(ToolchainResolution))]
[JsonSerializable(typeof(ToolchainCandidate))]
[JsonSerializable(typeof(VisualStudioInstance))]
[JsonSerializable(typeof(DeveloperEnvironmentRequest))]
[JsonSerializable(typeof(DeveloperEnvironmentSnapshot))]
[JsonSerializable(typeof(EnvironmentPlanRequest))]
[JsonSerializable(typeof(EnvironmentApplyRequest))]
[JsonSerializable(typeof(HostPreparationPlan))]
[JsonSerializable(typeof(HostPreparationResult))]
[JsonSerializable(typeof(ContextInspectRequest))]
[JsonSerializable(typeof(TargetResolveRequest))]
[JsonSerializable(typeof(WorkflowPlanRequest))]
[JsonSerializable(typeof(WorkflowPreflightRequest))]
[JsonSerializable(typeof(WorkflowPreflightResult))]
[JsonSerializable(typeof(WorkflowPreflightNode))]
[JsonSerializable(typeof(WorkflowPreflightDiagnostic))]
[JsonSerializable(typeof(List<WorkflowPreflightNode>))]
[JsonSerializable(typeof(ProfilePrepareRequest))]
[JsonSerializable(typeof(ProfilePreparation))]
[JsonSerializable(typeof(BenchmarkRecipeV1))]
[JsonSerializable(typeof(BenchmarkPrepareRequest))]
[JsonSerializable(typeof(BenchmarkPreparation))]
[JsonSerializable(typeof(ProfileRecipeV1))]
[JsonSerializable(typeof(ProfileRecipePrepareRequest))]
[JsonSerializable(typeof(ProfileRecipePreparation))]
[JsonSerializable(typeof(TestMatrixRecipeV1))]
[JsonSerializable(typeof(TestMatrixPrepareRequest))]
[JsonSerializable(typeof(TestMatrixPreparation))]
[JsonSerializable(typeof(BuildMatrixRecipeV1))]
[JsonSerializable(typeof(BuildMatrixPrepareRequest))]
[JsonSerializable(typeof(BuildMatrixPreparation))]
[JsonSerializable(typeof(BenchmarkMatrixRecipeV1))]
[JsonSerializable(typeof(BenchmarkMatrixPrepareRequest))]
[JsonSerializable(typeof(BenchmarkMatrixPreparation))]
[JsonSerializable(typeof(ProfileAnalyzeRequest))]
[JsonSerializable(typeof(ProfileAnalysis))]
[JsonSerializable(typeof(TargetPrepareRequest))]
[JsonSerializable(typeof(TargetPreparation))]
[JsonSerializable(typeof(ProfileReplayPrepareRequest))]
[JsonSerializable(typeof(ProfileReplayRequest))]
[JsonSerializable(typeof(ProfileReplayResponse))]
[JsonSerializable(typeof(CampaignMutationRequest))]
[JsonSerializable(typeof(CampaignWeakMemoryRequest))]
[JsonSerializable(typeof(CampaignFuzzSeedRequest))]
[JsonSerializable(typeof(CampaignFuzzRunRequest))]
[JsonSerializable(typeof(CampaignFuzzTriageRequest))]
[JsonSerializable(typeof(CampaignFuzzMergeRequest))]
[JsonSerializable(typeof(CampaignExecutionResult))]
[JsonSerializable(typeof(ProfileVariantRequest))]
[JsonSerializable(typeof(List<ProfileVariantRequest>))]
[JsonSerializable(typeof(RunGetRequest))]
[JsonSerializable(typeof(HistoryPolicy))]
[JsonSerializable(typeof(HistoryIndexStatus))]
[JsonSerializable(typeof(HistoryOperationRequest))]
[JsonSerializable(typeof(HistoryImportRequest))]
[JsonSerializable(typeof(HistoryImportResult))]
[JsonSerializable(typeof(HistoryRunsPageRequest))]
[JsonSerializable(typeof(HistoryRunPage))]
[JsonSerializable(typeof(HistoryRunsRequest))]
[JsonSerializable(typeof(HistoryShowRequest))]
[JsonSerializable(typeof(HistoryMetricsRequest))]
[JsonSerializable(typeof(HistoryCompareRequest))]
[JsonSerializable(typeof(HistoryDetectRequest))]
[JsonSerializable(typeof(ObservedBuildContext))]
[JsonSerializable(typeof(TargetResolutionView))]
[JsonSerializable(typeof(WorkflowPlanView))]
[JsonSerializable(typeof(WorkflowReportSnapshot))]
[JsonSerializable(typeof(OperationArtifactListRequest))]
[JsonSerializable(typeof(OperationArtifactChunkRequest))]
[JsonSerializable(typeof(OperationArtifactInfo))]
[JsonSerializable(typeof(OperationArtifactList))]
[JsonSerializable(typeof(OperationArtifactChunk))]
[JsonSerializable(typeof(List<HistoryRun>))]
[JsonSerializable(typeof(HistoryDetail))]
[JsonSerializable(typeof(List<HistoryMeasurement>))]
[JsonSerializable(typeof(List<HistoryDifference>))]
[JsonSerializable(typeof(HistoryDetectionReport))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSerializable(typeof(ExecutionStartRequest))]
[JsonSerializable(typeof(WorkflowStartRequest))]
[JsonSerializable(typeof(OperationCancelRequest))]
[JsonSerializable(typeof(OperationCancelResult))]
[JsonSerializable(typeof(WorkflowResumeRequest))]
[JsonSerializable(typeof(OperationEventsRequest))]
[JsonSerializable(typeof(OperationEventsPage))]
[JsonSerializable(typeof(List<WorkflowEvent>))]
[JsonSerializable(typeof(WorkflowEvent))]
[JsonSerializable(typeof(RecoveryInspectRequest))]
[JsonSerializable(typeof(RecoveryReconcileRequest))]
[JsonSerializable(typeof(RecoveryInspectionView))]
[JsonSerializable(typeof(OperationFindRequest))]
[JsonSerializable(typeof(OperationGetRequest))]
[JsonSerializable(typeof(OperationListRequest))]
[JsonSerializable(typeof(OperationListPage))]
[JsonSerializable(typeof(List<OperationReference>))]
[JsonSerializable(typeof(OperationOverview))]
[JsonSerializable(typeof(OperationNodeView))]
[JsonSerializable(typeof(List<OperationNodeView>))]
[JsonSerializable(typeof(OperationReference))]
[JsonSerializable(typeof(ApplicationError))]
[JsonSerializable(typeof(StreamJsonRpc.Protocol.CommonErrorData))]
// Incoming $/cancelRequest binds the id to StreamJsonRpc.RequestId.
// Keep it available to the reflection-free Host formatter as well.
[JsonSerializable(typeof(StreamJsonRpc.RequestId))]
public partial class HostJson : JsonSerializerContext;