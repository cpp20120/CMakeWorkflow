#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public sealed record HostPreparationResult(
    int SchemaVersion,
    bool Ready,
    ToolingResult? AppliedTools,
    HostPreparationPlan Checked,
    IReadOnlyList<HostSettingChange>? AppliedSystemChanges = null,
    string? AuditPath = null);
