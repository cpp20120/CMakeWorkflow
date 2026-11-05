using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History;
#pragma warning restore IDE0130

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
