using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Tooling.Environment;

public sealed record HostPreparationResult(
    int SchemaVersion,
    bool Ready,
    ToolingResult? AppliedTools,
    HostPreparationPlan Checked,
    IReadOnlyList<HostSettingChange>? AppliedSystemChanges = null,
    string? AuditPath = null);
