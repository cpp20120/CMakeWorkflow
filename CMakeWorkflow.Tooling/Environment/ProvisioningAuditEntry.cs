using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace CMakeWorkflow.Tooling;

public sealed record ProvisioningAuditEntry(
    DateTimeOffset At,
    string ApprovalSha256,
    string RequirementsSha256,
    string Status,
    IReadOnlyList<HostSettingChange> Changes,
    string? Error);
