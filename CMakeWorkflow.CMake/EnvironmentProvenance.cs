using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CMakeWorkflow;

public sealed record HostEnvironment(string? HostId, string? CpuModel, string Architecture,
    int LogicalProcessors, int AvailableProcessors, int? PhysicalPackages, int? PhysicalCores,
    string OperatingSystem, string KernelVersion, string? Affinity, string? Governor,
    string Fingerprint);

public sealed record BuildEnvironment(string Directory, string? Configuration, string? Generator,
    string? Compiler, string? CompilerId, string? CompilerVersion, string? Flags,
    string Fingerprint);

internal static class EnvironmentProvenance
{
    internal static string FingerprintOverrides(IReadOnlyDictionary<string, string> values) =>
        Hash(string.Concat(values.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key.Length + ":" + x.Key + x.Value.Length + ":" + x.Value)));

    internal static HostEnvironment CaptureHost()
    {
        var linux = OperatingSystem.IsLinux();
        var mac = OperatingSystem.IsMacOS();
        var cpuInfo = linux && File.Exists("/proc/cpuinfo") ? File.ReadAllLines("/proc/cpuinfo") : [];
        var cpuModel = linux ? Field(cpuInfo, "model name") : mac
            ? Sysctl("machdep.cpu.brand_string") : Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        var logical = linux ? cpuInfo.Count(x => x.StartsWith("processor", StringComparison.Ordinal)
            && x.Contains(':')) : mac && int.TryParse(Sysctl("hw.logicalcpu"), out var macLogical)
                ? macLogical : Environment.ProcessorCount;
        if (logical == 0) logical = Environment.ProcessorCount;
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        var packages = new HashSet<string>(StringComparer.Ordinal);
        string? package = null, core = null;
        foreach (var line in cpuInfo.Append(""))
        {
            if (line.Length == 0)
            {
                if (package is not null && core is not null)
                { packages.Add(package); pairs.Add(package + ":" + core); }
                package = core = null;
            }
            else if (line.StartsWith("physical id", StringComparison.Ordinal)) package = Value(line);
            else if (line.StartsWith("core id", StringComparison.Ordinal)) core = Value(line);
        }
        var kernel = linux && File.Exists("/proc/sys/kernel/osrelease")
            ? File.ReadAllText("/proc/sys/kernel/osrelease").Trim() : mac
                ? Sysctl("kern.osrelease") ?? Environment.OSVersion.VersionString
                : Environment.OSVersion.VersionString;
        var affinity = linux ? Field(File.ReadAllLines("/proc/self/status"), "Cpus_allowed_list")
            : ProcessAffinity();
        var governorPath = "/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor";
        var governor = linux && File.Exists(governorPath) ? File.ReadAllText(governorPath).Trim() : null;
        var os = RuntimeInformation.OSDescription;
        var architecture = RuntimeInformation.OSArchitecture.ToString();
        var packageCount = linux ? packages.Count == 0 ? (int?)null : packages.Count
            : mac && int.TryParse(Sysctl("hw.packages"), out var macPackages) ? macPackages : null;
        var coreCount = linux ? pairs.Count == 0 ? (int?)null : pairs.Count
            : mac && int.TryParse(Sysctl("hw.physicalcpu"), out var macCores) ? macCores : null;
        var fingerprint = Hash(string.Join("\n", cpuModel, architecture, logical,
            Environment.ProcessorCount, packageCount, coreCount, os, kernel, affinity, governor));
        return new(HostId(), cpuModel, architecture, logical, Environment.ProcessorCount,
            packageCount, coreCount,
            os, kernel, affinity, governor, fingerprint);
    }

    private static string? ProcessAffinity()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return null;
        try { return "0x" + Process.GetCurrentProcess().ProcessorAffinity.ToInt64().ToString("X"); }
        catch (PlatformNotSupportedException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static string? Sysctl(string name)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/sbin/sysctl")
            {
                ArgumentList = { "-n", name }, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false
            });
            if (process is null || !process.WaitForExit(2000) || process.ExitCode != 0)
            { if (process is { HasExited: false }) process.Kill(); return null; }
            return process.StandardOutput.ReadToEnd().Trim();
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (IOException) { return null; }
    }

    internal static BuildEnvironment? CaptureBuild(string directory, string? configuration)
    {
        var cache = Path.Combine(directory, "CMakeCache.txt");
        if (!File.Exists(cache)) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(cache))
        {
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':'); var equals = line.IndexOf('=');
            if (colon > 0 && equals > colon) values[line[..colon]] = line[(equals + 1)..];
        }
        string? Get(string key) => values.GetValueOrDefault(key);
        var config = configuration ?? Get("CMAKE_BUILD_TYPE");
        var flags = (Get("CMAKE_CXX_FLAGS") ?? "") + " " +
            (config is null ? "" : Get("CMAKE_CXX_FLAGS_" + config.ToUpperInvariant()) ?? "");
        var compilerId = (string?)null; var compilerVersion = (string?)null;
        var cmakeFiles = Path.Combine(directory, "CMakeFiles");
        if (Directory.Exists(cmakeFiles))
            foreach (var file in Directory.EnumerateFiles(cmakeFiles, "CMakeCXXCompiler.cmake", SearchOption.AllDirectories).Take(1))
                foreach (var line in File.ReadLines(file))
                {
                    if (line.StartsWith("set(CMAKE_CXX_COMPILER_ID ", StringComparison.Ordinal)) compilerId = CMakeValue(line);
                    if (line.StartsWith("set(CMAKE_CXX_COMPILER_VERSION ", StringComparison.Ordinal)) compilerVersion = CMakeValue(line);
                }
        var generator = Get("CMAKE_GENERATOR"); var compiler = Get("CMAKE_CXX_COMPILER");
        var fingerprint = Hash(string.Join("\n", config, generator, compiler, compilerId,
            compilerVersion, flags.Trim()));
        return new(directory, config, generator, compiler, compilerId, compilerVersion,
            flags.Trim(), fingerprint);
    }

    private static string? CMakeValue(string line)
    {
        var first = line.IndexOf('"'); var last = line.LastIndexOf('"');
        return first >= 0 && last > first ? line[(first + 1)..last] : null;
    }
    private static string? Value(string line)
    {
        var colon = line.IndexOf(':'); return colon < 0 ? null : line[(colon + 1)..].Trim();
    }
    private static string? Field(IEnumerable<string> lines, string name) =>
        lines.FirstOrDefault(x => x.StartsWith(name + "\t", StringComparison.Ordinal)
            || x.StartsWith(name + ":", StringComparison.Ordinal)) is { } line ? Value(line) : null;
    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string? HostId()
    {
        try
        {
            var stateDirectory = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_STATE_DIR");
            var path = stateDirectory is { Length: > 0 }
                ? Path.Combine(Path.GetFullPath(stateDirectory), "host-id")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CMakeWorkflow", "host-id");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None);
                    var bytes = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"));
                    stream.Write(bytes);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (IOException) when (File.Exists(path)) { }
            }
            var id = File.ReadAllText(path).Trim();
            return Guid.TryParseExact(id, "N", out _) ? id : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
