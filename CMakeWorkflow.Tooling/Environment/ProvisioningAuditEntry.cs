#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public sealed record ProvisioningAuditEntry(
    DateTimeOffset At,
    string ApprovalSha256,
    string RequirementsSha256,
    string Status,
    IReadOnlyList<HostSettingChange> Changes,
    string? Error);
