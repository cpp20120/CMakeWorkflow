using CMakeWorkflow.Tooling.Environment;

namespace CMakeWorkflow.Tooling.Toolchains;

public static class WindowsDevelopmentDoctor
{
    public static async Task<IReadOnlyList<HostFinding>> InspectAsync(HostRequirements requirements,
        IWindowsDevelopmentProbe probe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(requirements);
        ct.ThrowIfCancellationRequested();
        List<HostFinding> findings = [];
        if (requirements.RequireWindowsMsvc)
        {
            string? path = await probe.MsvcToolsetAsync(ct);
            findings.Add(new("windows_msvc", path is null ? "missing" : "ready", path,
                "Visual Studio C++ x64 toolset",
                "Install the Desktop development with C++ workload (MSVC component)."));
        }

        if (requirements.RequireWindowsSdk)
        {
            string? path = probe.WindowsSdk();
            findings.Add(new("windows_sdk", path is null ? "missing" : "ready", path,
                "Windows SDK with x64 tools",
                "Install Windows 10/11 SDK through Visual Studio Installer or standalone SDK."));
        }

        if (requirements.RequireWindowsWpr)
        {
            string? path = probe.WindowsPerformanceRecorder();
            findings.Add(new("windows_wpr", path is null ? "missing" : "ready", path,
                "Windows Performance Recorder",
                "Install Windows Performance Toolkit from the Windows ADK; discovery does not prove ETW trace privileges."));
            // Access control varies by trace provider and profile. No read-only API can establish universal ETW rights.
            findings.Add(new("windows_etw_permissions", "unverified", null,
                "Permission for selected ETW trace session",
                "WPR presence does not establish provider/session permissions; validate with an explicit, separately authorized trace smoke test."));
        }

        return findings;
    }
}
