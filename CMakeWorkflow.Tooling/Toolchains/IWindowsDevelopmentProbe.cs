using System.Diagnostics;
using Microsoft.Win32;

namespace CMakeWorkflow.Tooling;

// Windows discovery only. Never executes setup programs, requests elevation, or starts tracing.
public interface IWindowsDevelopmentProbe
{
    string? MsvcToolset();
    string? WindowsSdk();
    string? WindowsPerformanceRecorder();
}
