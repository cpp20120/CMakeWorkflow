using System.Diagnostics;
using Microsoft.Win32;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public sealed class SystemWindowsDevelopmentProbe : IWindowsDevelopmentProbe
{
    public string? MsvcToolset()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        ];
        foreach (string? root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string tool = Path.Combine(root, "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(tool))
            {
                continue;
            }

            try
            {
                using Process? process = Process.Start(new ProcessStartInfo(tool)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList =
                    {
                        "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                        "-property", "installationPath"
                    }
                });
                if (process is null)
                {
                    continue;
                }

                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(5000))
                {
                    process.Kill();
                    continue;
                }

                if (process.ExitCode != 0)
                {
                    continue;
                }

                string installation = output.Trim();
                if (installation.Length == 0)
                {
                    continue;
                }

                string vc = Path.Combine(installation, "VC", "Tools", "MSVC");
                if (!Directory.Exists(vc))
                {
                    continue;
                }

                foreach (string? version in Directory.EnumerateDirectories(vc)
                             .OrderByDescending(x => x, StringComparer.Ordinal))
                {
                    string cl = Path.Combine(version, "bin", "Hostx64", "x64", "cl.exe");
                    if (File.Exists(cl))
                    {
                        return cl;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }

        return null;
    }

    public string? WindowsSdk()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using RegistryKey? roots = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
            string? dir = roots?.GetValue("KitsRoot10") as string;
            if (string.IsNullOrEmpty(dir))
            {
                return null;
            }

            string bin = Path.Combine(dir, "bin");
            if (!Directory.Exists(bin))
            {
                return null;
            }

            foreach (string? version in Directory.EnumerateDirectories(bin)
                         .OrderByDescending(x => x, StringComparer.Ordinal))
            {
                string rc = Path.Combine(version, "x64", "rc.exe");
                string mt = Path.Combine(version, "x64", "mt.exe");
                if (File.Exists(rc) && File.Exists(mt))
                {
                    return version;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return null;
    }

    public string? WindowsPerformanceRecorder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        List<string> candidates = [];
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(system))
        {
            candidates.Add(Path.Combine(system, "wpr.exe"));
        }

        string kits = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(kits))
        {
            candidates.Add(Path.Combine(kits, "Windows Kits", "10", "Windows Performance Toolkit", "wpr.exe"));
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(directory, "wpr.exe"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}