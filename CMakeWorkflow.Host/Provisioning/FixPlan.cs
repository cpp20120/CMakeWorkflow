using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Host.Provisioning;

public sealed record FixPlan(
    int SchemaVersion,
    string PlanId,
    DateTimeOffset ExpiresAt,
    ToolingPlan Plan,
    string? RunDirectory = null,
    string? RunSnapshot = null);
