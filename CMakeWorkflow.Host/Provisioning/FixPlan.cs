using CMakeWorkflow.Tooling.Tools;
using CMakeWorkflow.Tooling.VisualStudio;

namespace CMakeWorkflow.Host.Provisioning;

public sealed record FixPlan(
    int SchemaVersion,
    string PlanId,
    DateTimeOffset ExpiresAt,
    ToolingPlan? Plan,
    string? RunDirectory = null,
    string? RunSnapshot = null,
    VisualStudioSetupPlan? VisualStudioPlan = null);
