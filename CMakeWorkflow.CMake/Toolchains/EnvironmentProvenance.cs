using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

internal static class EnvironmentProvenance
{
    internal static string FingerprintOverrides(IReadOnlyDictionary<string, string> values) =>
        Hash(string.Concat(values.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key.Length + ":" + x.Key + x.Value.Length + ":" + x.Value)));

    internal static HostEnvironment CaptureHost()
    {
        bool linux = OperatingSystem.IsLinux();
        bool mac = OperatingSystem.IsMacOS();
        string[] cpuInfo = linux && File.Exists("/proc/cpuinfo") ? File.ReadAllLines("/proc/cpuinfo") : [];
        string? cpuModel = linux
            ? Field(cpuInfo, "model name")
            : mac
                ? Sysctl("machdep.cpu.brand_string")
                : Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        int logical = linux
            ? cpuInfo.Count(x => x.StartsWith("processor", StringComparison.Ordinal)
                                 && x.Contains(':', StringComparison.Ordinal))
            : mac && int.TryParse(Sysctl("hw.logicalcpu"), out int macLogical)
                ? macLogical
                : Environment.ProcessorCount;
        if (logical == 0)
        {
            logical = Environment.ProcessorCount;
        }

        HashSet<string> pairs = new(StringComparer.Ordinal);
        HashSet<string> packages = new(StringComparer.Ordinal);
        string? package = null, core = null;
        foreach (string? line in cpuInfo.Append(""))
        {
            if (line.Length == 0)
            {
                if (package is not null && core is not null)
                {
                    _ = packages.Add(package);
                    _ = pairs.Add(package + ":" + core);
                }

                package = core = null;
            }
            else if (line.StartsWith("physical id", StringComparison.Ordinal))
            {
                package = Value(line);
            }
            else if (line.StartsWith("core id", StringComparison.Ordinal))
            {
                core = Value(line);
            }
        }

        string kernel = linux && File.Exists("/proc/sys/kernel/osrelease")
            ? File.ReadAllText("/proc/sys/kernel/osrelease").Trim()
            : mac
                ? Sysctl("kern.osrelease") ?? Environment.OSVersion.VersionString
                : Environment.OSVersion.VersionString;
        string? affinity = linux
            ? Field(File.ReadAllLines("/proc/self/status"), "Cpus_allowed_list")
            : ProcessAffinity();
        string governorPath = "/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor";
        string? governor = linux && File.Exists(governorPath) ? File.ReadAllText(governorPath).Trim() : null;
        string os = RuntimeInformation.OSDescription;
        string architecture = RuntimeInformation.OSArchitecture.ToString();
        int? packageCount = linux ? packages.Count == 0 ? null : packages.Count
            : mac && int.TryParse(Sysctl("hw.packages"), out int macPackages) ? macPackages : null;
        int? coreCount = linux ? pairs.Count == 0 ? null : pairs.Count
            : mac && int.TryParse(Sysctl("hw.physicalcpu"), out int macCores) ? macCores : null;
        string fingerprint = Hash(string.Join("\n", cpuModel, architecture, logical,
            Environment.ProcessorCount, packageCount, coreCount, os, kernel, affinity, governor));
        return new(HostId(), cpuModel, architecture, logical, Environment.ProcessorCount,
            packageCount, coreCount,
            os, kernel, affinity, governor, fingerprint);
    }

    private static string? ProcessAffinity()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return "0x" + Process.GetCurrentProcess().ProcessorAffinity.ToInt64().ToString("X", System.Globalization.CultureInfo.CurrentCulture);
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? Sysctl(string name)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("/usr/sbin/sysctl")
            {
                ArgumentList = { "-n", name },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null || !process.WaitForExit(2000) || process.ExitCode != 0)
            {
                if (process is { HasExited: false })
                {
                    process.Kill();
                }

                return null;
            }

            return process.StandardOutput.ReadToEnd().Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static BuildEnvironment? CaptureBuild(string directory, string? configuration)
    {
        string cache = Path.Combine(directory, "CMakeCache.txt");
        if (!File.Exists(cache))
        {
            return null;
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(cache))
        {
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#'))
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (colon > 0 && equals > colon)
            {
                values[line[..colon]] = line[(equals + 1)..];
            }
        }

        string? Get(string key) => values.GetValueOrDefault(key);
        string? config = configuration ?? Get("CMAKE_BUILD_TYPE");
        string flags = (Get("CMAKE_CXX_FLAGS") ?? "") + " " +
                    (config is null ? "" : Get("CMAKE_CXX_FLAGS_" + config.ToUpperInvariant()) ?? "");
        string? compilerId = null;
        string? compilerVersion = null;
        string cmakeFiles = Path.Combine(directory, "CMakeFiles");
        if (Directory.Exists(cmakeFiles))
        {
            foreach (string? file in Directory
                         .EnumerateFiles(cmakeFiles, "CMakeCXXCompiler.cmake", SearchOption.AllDirectories).Take(1))
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (line.StartsWith("set(CMAKE_CXX_COMPILER_ID ", StringComparison.Ordinal))
                    {
                        compilerId = CMakeValue(line);
                    }

                    if (line.StartsWith("set(CMAKE_CXX_COMPILER_VERSION ", StringComparison.Ordinal))
                    {
                        compilerVersion = CMakeValue(line);
                    }
                }
            }
        }

        string? generator = Get("CMAKE_GENERATOR");
        string? compiler = Get("CMAKE_CXX_COMPILER");
        string fingerprint = Hash(string.Join("\n", config, generator, compiler, compilerId,
            compilerVersion, flags.Trim()));
        return new(directory, config, generator, compiler, compilerId, compilerVersion,
            flags.Trim(), fingerprint);
    }

    private static string? CMakeValue(string line)
    {
        int first = line.IndexOf('"', StringComparison.Ordinal);
        int last = line.LastIndexOf('"');
        return first >= 0 && last > first ? line[(first + 1)..last] : null;
    }

    private static string? Value(string line)
    {
        int colon = line.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? null : line[(colon + 1)..].Trim();
    }

    private static string? Field(IEnumerable<string> lines, string name) =>
        lines.FirstOrDefault(x => x.StartsWith(name + "\t", StringComparison.Ordinal)
                                  || x.StartsWith(name + ":", StringComparison.Ordinal)) is { } line
            ? Value(line)
            : null;


#pragma warning disable CA1308 // Lowercase values are part of the existing persisted hash, identifier, or command protocol.
    private static string Hash(string value) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
#pragma warning restore CA1308


    private static string? HostId()
    {
        try
        {
            string? stateDirectory = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_STATE_DIR");
            string path = stateDirectory is { Length: > 0 }
                ? Path.Combine(Path.GetFullPath(stateDirectory), "host-id")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CMakeWorkflow", "host-id");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                try
                {
                    using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None);
                    byte[] bytes = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"));
                    stream.Write(bytes);
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(path,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }
                }
                catch (IOException) when (File.Exists(path))
                {
                }
            }

            string id = File.ReadAllText(path).Trim();
            return Guid.TryParseExact(id, "N", out _) ? id : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}