namespace CMakeWorkflow.Profiling.Capture;

public sealed record ProfileRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    string OutputDirectory,
    string CaptureId,
    string Mode,
    TimeSpan Timeout,
    string? CpuSet = null,
    string? CpuAffinityLauncher = null);
