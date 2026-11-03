using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace CMakeWorkflow.Tooling;

public sealed record HostPreparationPlan(
    int SchemaVersion,
    string RequirementsSha256,
    string Source,
    ToolingPlan Tools,
    IReadOnlyList<HostFinding> Findings,
    bool Ready,
    string ApprovalSha256,
    IReadOnlyList<HostSettingChange>? SystemChanges = null,
    IReadOnlyList<string>? CpuPolicyPaths = null);
