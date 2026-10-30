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

public static class WindowsDevelopmentDoctor
{
    public static IReadOnlyList<HostFinding> Inspect(HostRequirements requirements, IWindowsDevelopmentProbe probe)
    {
        var findings = new List<HostFinding>();
        if (requirements.RequireWindowsMsvc)
        {
            var path = probe.MsvcToolset();
            findings.Add(new("windows_msvc", path is null ? "missing" : "ready", path,
                "Visual Studio C++ x64 toolset", "Install the Desktop development with C++ workload (MSVC component)."));
        }
        if (requirements.RequireWindowsSdk)
        {
            var path = probe.WindowsSdk();
            findings.Add(new("windows_sdk", path is null ? "missing" : "ready", path,
                "Windows SDK with x64 tools", "Install Windows 10/11 SDK through Visual Studio Installer or standalone SDK."));
        }
        if (requirements.RequireWindowsWpr)
        {
            var path = probe.WindowsPerformanceRecorder();
            findings.Add(new("windows_wpr", path is null ? "missing" : "ready", path,
                "Windows Performance Recorder", "Install Windows Performance Toolkit from the Windows ADK; discovery does not prove ETW trace privileges."));
            // Access control varies by trace provider and profile. No read-only API can establish universal ETW rights.
            findings.Add(new("windows_etw_permissions", "unverified", null,
                "Permission for selected ETW trace session", "WPR presence does not establish provider/session permissions; validate with an explicit, separately authorized trace smoke test."));
        }
        return findings;
    }
}

public sealed class SystemWindowsDevelopmentProbe : IWindowsDevelopmentProbe
{
    public string? MsvcToolset()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tool = Path.Combine(root, "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(tool)) continue;
            try
            {
                using var process = Process.Start(new ProcessStartInfo(tool)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    ArgumentList = { "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath" }
                });
                if (process is null) continue;
                var output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(5000)) { process.Kill(); continue; }
                if (process.ExitCode != 0) continue;
                var installation = output.Trim();
                if (installation.Length == 0) continue;
                var vc = Path.Combine(installation, "VC", "Tools", "MSVC");
                if (!Directory.Exists(vc)) continue;
                foreach (var version in Directory.EnumerateDirectories(vc).OrderByDescending(x => x, StringComparer.Ordinal))
                {
                    var cl = Path.Combine(version, "bin", "Hostx64", "x64", "cl.exe");
                    if (File.Exists(cl)) return cl;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
        return null;
    }

    public string? WindowsSdk()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var roots = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
            var dir = roots?.GetValue("KitsRoot10") as string;
            if (string.IsNullOrEmpty(dir)) return null;
            var bin = Path.Combine(dir, "bin");
            if (!Directory.Exists(bin)) return null;
            foreach (var version in Directory.EnumerateDirectories(bin).OrderByDescending(x => x, StringComparer.Ordinal))
            {
                var rc = Path.Combine(version, "x64", "rc.exe");
                var mt = Path.Combine(version, "x64", "mt.exe");
                if (File.Exists(rc) && File.Exists(mt)) return version;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }

    public string? WindowsPerformanceRecorder()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var candidates = new List<string>();
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(system)) candidates.Add(Path.Combine(system, "wpr.exe"));
        var kits = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(kits)) candidates.Add(Path.Combine(kits, "Windows Kits", "10", "Windows Performance Toolkit", "wpr.exe"));
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(directory, "wpr.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }
}
