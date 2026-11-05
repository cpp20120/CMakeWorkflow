#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

// Windows discovery only. Never executes setup programs, requests elevation, or starts tracing.
public interface IWindowsDevelopmentProbe
{
    string? MsvcToolset();
    string? WindowsSdk();
    string? WindowsPerformanceRecorder();
}
