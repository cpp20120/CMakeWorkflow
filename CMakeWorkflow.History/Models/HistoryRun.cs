
// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.History.Models;
#pragma warning restore IDE0130

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

List<HistoryBuildEnvironment>? BuildEnvironments = null);
