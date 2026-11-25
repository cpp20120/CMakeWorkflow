using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using CMakeWorkflow.Contracts.Workflows;
// SDK wire projections of CMakeWorkflow.History records. Keep properties/constructors in sync with Host DTOs.
// Do NOT reference CMakeWorkflow.History from the SDK: that assembly also contains SQLite storage.
namespace CMakeWorkflow.Client.History;

public sealed record HistoryArtifact(
    string RunId,
    string? NodeId,
    string Type,
    string Path,
    string? Sha256,
    string? Metadata,
    bool Available = true);

public sealed record HistoryBuildEnvironment(
    string Directory,
    string? Configuration,
    string? Generator,
    string? Compiler,
    string? CompilerId,
    string? CompilerVersion,
    string? Flags,
    string Fingerprint);

[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.History.HistoryDetail(Nodes, Measurements, Artifacts) participates in CMakeWorkflow.Client.History.HistoryService.ShowAsync; retain these concrete collection parameter types.")]
public sealed record HistoryDetail(
    HistoryRun Run,
    [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.History.HistoryDetail.Nodes (List<HistoryNode>) is exposed through CMakeWorkflow.Client.History.HistoryService.ShowAsync; retain this Client SDK property signature.")]
List<HistoryNode> Nodes,
    [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.History.HistoryDetail.Measurements (List<HistoryMeasurement>) is exposed through CMakeWorkflow.Client.History.HistoryService.ShowAsync; retain this Client SDK property signature.")]
List<HistoryMeasurement> Measurements,
    [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.History.HistoryDetail.Artifacts (List<HistoryArtifact>) is exposed through CMakeWorkflow.Client.History.HistoryService.ShowAsync; retain this Client SDK property signature.")]
List<HistoryArtifact> Artifacts);

public sealed record HistoryHostEnvironment(
    string? HostId,
    string? CpuModel,
    string? Architecture,
    int? LogicalProcessors,
    int? AvailableProcessors,
    int? PhysicalPackages,
    int? PhysicalCores,
    string? OperatingSystem,
    string? KernelVersion,
    string? Affinity,
    string? Governor,
    string? Fingerprint);

public sealed record HistoryMeasurement(
    string RunId,
    string? NodeId,
    string Metric,
    string Unit,
    double Value,
    string? Variant,
    string? Scenario,
    string Source,
    string Method,
    int SampleIndex,
    string? Workload,
    string? Environment)
{
    /// <summary>Declared improvement direction for a benchmark metric, when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Direction { get; init; }
    /// <summary>Resolved recipe metadata for a benchmark trial, when available.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenchmarkTrial? BenchmarkTrial { get; init; }
}

public sealed record HistoryNode(
    string RunId,
    string Id,
    string Kind,
    string Status,
    double? DurationSeconds,
    string? Dependencies,
    string? BuildDirectory,
    string? Configuration,
    string? Target);

[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.History.HistoryRun(BuildEnvironments) participates in CMakeWorkflow.Client.History.HistoryService.RunsAsync; retain these concrete collection parameter types.")]
public sealed record HistoryRun(
    string Id,
    string Directory,
    string Format,
    string? Workflow,
    string? ManifestHash,
    string? StartedAt,
    string? FinishedAt,
    string Status,
    string? Revision,
    string SourceHash,
    bool SourceAvailable = true,
    string? HostId = null,
    string? EnvironmentFingerprint = null,
    HistoryHostEnvironment? Environment = null,
    [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.History.HistoryRun.BuildEnvironments (List<HistoryBuildEnvironment>?) is exposed through CMakeWorkflow.Client.History.HistoryService.RunsAsync; retain this Client SDK property signature.")]
List<HistoryBuildEnvironment>? BuildEnvironments = null)
{
    public string? SourceSnapshotId { get; init; }
}

public sealed record HistoryStats(
    int Count,
    double Median,
    double Min,
    double Max,
    double Mean,
    double StandardDeviation);

public sealed record HistoryDifference(
    string Metric,
    string Unit,
    string? Variant,
    string? Scenario,
    string Method,
    string Status,
    string? Reason,
    HistoryStats? Baseline,
    HistoryStats? Candidate,
    double? AbsoluteChange,
    double? PercentChange)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BaselineRunId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CandidateRunId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BaselineVariant { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CandidateVariant { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? BaselineEnvironment { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CandidateEnvironment { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? RunsSucceeded { get; init; }
}

public sealed record HistoryDetection(
    string Metric,
    string Unit,
    string? Variant,
    string? Scenario,
    string Method,
    string Status,
    string Reason,
    HistoryStats? Baseline,
    HistoryStats? Candidate,
    double? AbsoluteChange,
    double? PercentChange,
    double? PValue,
    double? AdjustedPValue,
    int? Permutations,
    bool? Exact);

[method: SuppressMessage("Design", "CA1002", Justification = "Client SDK constructor CMakeWorkflow.Client.History.HistoryDetectionReport(Results) participates in CMakeWorkflow.Client.History.HistoryService.DetectAsync; retain these concrete collection parameter types.")]
public sealed record HistoryDetectionReport(
    int SchemaVersion,
    string BaselineRun,
    string CandidateRun,
    string Metric,
    string Direction,
    double MinimumEffectPercent,
    double Alpha,
    string Test,
    [property: SuppressMessage("Design", "CA1002", Justification = "CMakeWorkflow.Client.History.HistoryDetectionReport.Results (List<HistoryDetection>) is exposed through CMakeWorkflow.Client.History.HistoryService.DetectAsync; retain this Client SDK property signature.")]
List<HistoryDetection> Results);
