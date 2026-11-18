namespace CMakeWorkflow.Tooling.Toolchains;

// Windows discovery only. Never executes setup programs, requests elevation, or starts tracing.
public interface IWindowsDevelopmentProbe
{
    Task<string?> MsvcToolsetAsync(CancellationToken ct);
    string? WindowsSdk();
    string? WindowsPerformanceRecorder();
}
