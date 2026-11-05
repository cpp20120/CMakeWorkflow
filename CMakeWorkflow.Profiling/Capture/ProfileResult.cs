#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling;
#pragma warning restore IDE0130

public sealed record ProfileResult(
    string Backend,
    string Mode,
    int ExitCode,
    string RawTrace,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string CaptureStatus,
    ProfileCapabilities Capabilities,
    string Output,
    int? TargetProcessId = null,
    DateTimeOffset? TargetStartedAt = null,
    DateTimeOffset? TargetFinishedAt = null);
