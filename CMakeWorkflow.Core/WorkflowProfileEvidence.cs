namespace CMakeWorkflow.Core;

/// <summary>Structured evidence supplied by a profiler after a capture completes.</summary>
public sealed record WorkflowProfileEvidence(
    string CaptureId, string Backend, string Mode, string Status, int ExitCode,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string Executable, string ExecutableSha256,
    string RawTrace, string RawTraceSha256, string? CounterConfiguration, string? CounterConfigurationSha256,
    int? TargetProcessId, DateTimeOffset? TargetStartedAt, DateTimeOffset? TargetFinishedAt,
    string EventSource, string LaunchModel, string StartGuarantee, string Attribution,
    IReadOnlyList<string> SupportedModes, IReadOnlyList<string> RawFormats,
    string? CpuSet, string? CpuAffinityLauncher, string? MemoryStallEvent, string? PmcEvents,
    int PmcInterval, long TimeoutTicks);
