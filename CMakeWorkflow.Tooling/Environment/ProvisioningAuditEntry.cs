// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Environment;
#pragma warning restore IDE0130

public sealed record ProvisioningAuditEntry(
    DateTimeOffset At,
    string ApprovalSha256,
    string RequirementsSha256,
    string Status,
    IReadOnlyList<HostSettingChange> Changes,
    string? Error);
