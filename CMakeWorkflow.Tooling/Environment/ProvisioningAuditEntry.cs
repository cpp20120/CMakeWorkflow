namespace CMakeWorkflow.Tooling.Environment;

public sealed record ProvisioningAuditEntry(
    DateTimeOffset At,
    string ApprovalSha256,
    string RequirementsSha256,
    string Status,
    IReadOnlyList<HostSettingChange> Changes,
    string? Error);
