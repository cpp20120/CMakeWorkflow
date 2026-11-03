using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace CMakeWorkflow.Tooling;

public sealed record HostPreparationResult(
    int SchemaVersion,
    bool Ready,
    ToolingResult? AppliedTools,
    HostPreparationPlan Checked,
    IReadOnlyList<HostSettingChange>? AppliedSystemChanges = null,
    string? AuditPath = null);
