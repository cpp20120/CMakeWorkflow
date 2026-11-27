using System.Text.Json.Serialization;

namespace CMakeWorkflow.Contracts.Workflows;

/// <summary>Inspect, explicitly install, or verify vcpkg packages for an already-staged experiment.</summary>
public sealed record ExperimentDependencyRequest(string ExperimentId, string Commit, string SourceSnapshotId,
    string ManifestSha256, string PlanSha256, string? ApprovalSha256, bool VerifyInstalled)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Binary { get; init; }
}

/// <summary>Exact dependency plan bytes and verified installed package-tree identity, if requested.</summary>
public sealed record ExperimentDependencyResult(string PlanSha256, string Plan, string? InstalledTreeSha256)
{
    public string? DownloadTreeSha256 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReadOnlyMemory<byte>? BinaryPlan { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReceiptIdentity { get; init; }
}
