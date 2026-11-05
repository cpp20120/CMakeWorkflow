using CMakeWorkflow.Tooling;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

public sealed record FixPlan(
    int SchemaVersion,
    string PlanId,
    DateTimeOffset ExpiresAt,
    ToolingPlan Plan,
    string? RunDirectory = null,
    string? RunSnapshot = null);
