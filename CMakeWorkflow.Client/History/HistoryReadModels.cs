using System.Diagnostics.CodeAnalysis;
// SDK wire projections of CMakeWorkflow.History records. Keep properties/constructors in sync with Host DTOs.
// Do NOT reference CMakeWorkflow.History from the SDK: that assembly also contains SQLite storage.
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

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

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record HistoryDetail(
    HistoryRun Run,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<HistoryNode> Nodes,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<HistoryMeasurement> Measurements,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
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
    string? Environment);

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

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
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
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<HistoryBuildEnvironment>? BuildEnvironments = null);

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
    double? PercentChange);

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

[method: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
public sealed record HistoryDetectionReport(
    int SchemaVersion,
    string BaselineRun,
    string CandidateRun,
    string Metric,
    string Direction,
    double MinimumEffectPercent,
    double Alpha,
    string Test,
    [property: SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
List<HistoryDetection> Results);
