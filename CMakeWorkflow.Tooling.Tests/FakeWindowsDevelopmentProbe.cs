using CMakeWorkflow.Tooling.Toolchains;

// Test double for Windows discovery without requiring Windows or mutating host state.
namespace CMakeWorkflow.Tooling.Tests;

internal sealed class FakeWindowsDevelopmentProbe : IWindowsDevelopmentProbe
{
    public string? Msvc, Sdk, Wpr;

    public string? MsvcToolset() => Msvc;
    public string? WindowsSdk() => Sdk;
    public string? WindowsPerformanceRecorder() => Wpr;
}