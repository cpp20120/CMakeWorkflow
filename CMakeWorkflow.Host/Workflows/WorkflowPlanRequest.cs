// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Workflows;
#pragma warning restore IDE0130

public sealed record WorkflowPlanRequest(
    string Manifest,

#pragma warning disable CA1819 // Preserve this existing array-valued SDK/DTO property and its serialization contract.
string[]? Only = null
#pragma warning restore CA1819
,
    string? ControllerBuildDirectory = null);
