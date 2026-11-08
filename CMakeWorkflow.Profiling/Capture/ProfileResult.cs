namespace CMakeWorkflow.Profiling.Capture;

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
