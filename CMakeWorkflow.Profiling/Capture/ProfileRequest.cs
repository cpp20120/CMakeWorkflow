// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling.Capture;
#pragma warning restore IDE0130

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
