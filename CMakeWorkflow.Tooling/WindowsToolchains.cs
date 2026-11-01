using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CMakeWorkflow.Tooling;

// Discovery is evidence, not a promise that a particular compiler/SDK ABI works.
// Candidate IDs derive from role + canonical location; they are not client-supplied paths.
public sealed record ToolchainCandidate(string Id, string Role, string Flavor, string Provider,
    string Path, string? Version = null, string? VisualStudioInstanceId = null,
    string? EnvironmentProfileId = null);
public sealed record VisualStudioInstance(string Id, string Name, string Version,
    string InstallationPath, string? DeveloperCommandPath);
public sealed record ToolchainInventory(int SchemaVersion, string Platform,
    List<VisualStudioInstance> VisualStudio, List<ToolchainCandidate> Candidates,
    List<string> Diagnostics);
public sealed record ToolchainSelection(string? CMakeCandidateId = null, string? NinjaCandidateId = null,
    string? CompilerCandidateId = null, string? SdkCandidateId = null,
    string? EnvironmentProfileId = null, string Generator = "Ninja");
public sealed record ToolchainResolution(int SchemaVersion, string Status,
    ToolchainCandidate? CMake, ToolchainCandidate? Ninja, ToolchainCandidate? Compiler,
    ToolchainCandidate? Sdk, string? EnvironmentProfileId, List<string> Diagnostics);
public sealed record DeveloperEnvironmentRequest(string EnvironmentProfileId, string HostArchitecture = "x64",
    string TargetArchitecture = "x64", string? WindowsSdkVersion = null);
public sealed record DeveloperEnvironmentSnapshot(int SchemaVersion, string EnvironmentProfileId,
    string HostArchitecture, string TargetArchitecture, string Fingerprint,
    Dictionary<string, string> VisibleVariables, List<string> Diagnostics);

public static class WindowsToolchains
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    public static string CandidateId(string role, string path)
    {
        var data = Encoding.UTF8.GetBytes(role + "\0" + Path.GetFullPath(path).ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(data))[..24].ToLowerInvariant();
    }

    public static List<VisualStudioInstance> ParseVsWhere(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("vswhere response must be an array.");
        var result = new List<VisualStudioInstance>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            static string? Property(JsonElement item, string key) => item.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var id = Property(item, "instanceId");
            var path = Property(item, "installationPath");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                continue;
            var full = Path.GetFullPath(path);
            var devcmd = Path.Combine(full, "Common7", "Tools", "VsDevCmd.bat");
            result.Add(new(id, Property(item, "displayName") ?? id,
                Property(item, "installationVersion") ?? "unknown", full,
                File.Exists(devcmd) ? devcmd : null));
        }
        return result.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
    }

    public static async Task<ToolchainInventory> DiscoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var inventory = new ToolchainInventory(1, OperatingSystem.IsWindows() ? "windows" : "unsupported",
            [], [], []);
        if (!OperatingSystem.IsWindows())
        {
            inventory.Diagnostics.Add("Windows toolchain discovery is only available on Windows.");
            return inventory;
        }
        var seen = new HashSet<string>(Paths);
        void Add(string role, string flavor, string provider, string path, string? version = null,
            string? instanceId = null, string? environment = null)
        {
            if (!File.Exists(path) && !(role == "sdk" && Directory.Exists(path))) return;
            var full = Path.GetFullPath(path);
            var key = role + "\0" + full;
            if (!seen.Add(key) || inventory.Candidates.Count >= 512) return;
            inventory.Candidates.Add(new(CandidateId(role, full), role, flavor, provider, full,
                version, instanceId, environment));
        }
        var commandMap = new (string Role, string Flavor, string Name)[]
        {
            ("cmake", "cmake", "cmake.exe"), ("ninja", "ninja", "ninja.exe"),
            ("compiler", "clang-cl", "clang-cl.exe"), ("compiler", "clang", "clang++.exe"),
            ("compiler", "gcc", "g++.exe"), ("compiler", "msvc", "cl.exe"),
            ("msbuild", "msbuild", "MSBuild.exe")
        };
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var folder in pathEntries.Distinct(Paths))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (role, flavor, name) in commandMap)
            {
                try { Add(role, flavor, "path", System.IO.Path.Combine(folder.Trim('"'), name)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(Paths);
        foreach (var root in roots)
        {
            Add("compiler", "clang", "standalone", Path.Combine(root, "LLVM", "bin", "clang++.exe"));
            Add("compiler", "clang-cl", "standalone", Path.Combine(root, "LLVM", "bin", "clang-cl.exe"));
            Add("cmake", "cmake", "standalone", Path.Combine(root, "CMake", "bin", "cmake.exe"));
        }
        var locators = roots.Select(root => Path.Combine(root, "Microsoft Visual Studio", "Installer", "vswhere.exe"))
            .Concat(pathEntries.Select(path => Path.Combine(path.Trim('"'), "vswhere.exe")))
            .Distinct(Paths);
        foreach (var exe in locators)
        {
            if (!File.Exists(exe)) continue;
            try
            {
                var result = await RunBoundedAsync(exe,
                    ["-products", "*", "-prerelease", "-format", "json", "-utf8", "-nologo"],
                    TimeSpan.FromSeconds(8), ct);
                if (result.ExitCode != 0)
                {
                    inventory.Diagnostics.Add("vswhere failed (exit " + result.ExitCode + ").");
                    continue;
                }
                inventory.VisualStudio.AddRange(ParseVsWhere(result.Output));
                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or InvalidDataException or System.ComponentModel.Win32Exception or TimeoutException)
            { inventory.Diagnostics.Add("vswhere discovery failed: " + error.GetType().Name); }
        }
        foreach (var vs in inventory.VisualStudio)
        {
            ct.ThrowIfCancellationRequested();
            var profile = vs.DeveloperCommandPath is null ? null : "vs:" + vs.Id;
            var bundled = Path.Combine(vs.InstallationPath, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake");
            Add("cmake", "cmake", "visual-studio", Path.Combine(bundled, "CMake", "bin", "cmake.exe"),
                instanceId: vs.Id);
            Add("ninja", "ninja", "visual-studio", Path.Combine(bundled, "Ninja", "ninja.exe"),
                instanceId: vs.Id);
            Add("msbuild", "msbuild", "visual-studio",
                Path.Combine(vs.InstallationPath, "MSBuild", "Current", "Bin", "amd64", "MSBuild.exe"),
                instanceId: vs.Id);
            Add("msbuild", "msbuild", "visual-studio",
                Path.Combine(vs.InstallationPath, "MSBuild", "Current", "Bin", "MSBuild.exe"),
                instanceId: vs.Id);
            var vc = Path.Combine(vs.InstallationPath, "VC", "Tools", "MSVC");
            if (Directory.Exists(vc))
                foreach (var version in Directory.EnumerateDirectories(vc).OrderByDescending(x => x, StringComparer.Ordinal))
                    Add("compiler", "msvc", "visual-studio", Path.Combine(version, "bin", "Hostx64", "x64", "cl.exe"),
                        Path.GetFileName(version), vs.Id, profile);
            var llvm = Path.Combine(vs.InstallationPath, "VC", "Tools", "Llvm", "bin");
            Add("compiler", "clang-cl", "visual-studio", Path.Combine(llvm, "clang-cl.exe"),
                instanceId: vs.Id, environment: profile);
            Add("compiler", "clang", "visual-studio", Path.Combine(llvm, "clang++.exe"),
                instanceId: vs.Id, environment: profile);
            var llvm64 = Path.Combine(vs.InstallationPath, "VC", "Tools", "Llvm", "x64", "bin");
            Add("compiler", "clang-cl", "visual-studio", Path.Combine(llvm64, "clang-cl.exe"),
                instanceId: vs.Id, environment: profile);
            Add("compiler", "clang", "visual-studio", Path.Combine(llvm64, "clang++.exe"),
                instanceId: vs.Id, environment: profile);
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
                if (key?.GetValue("KitsRoot10") is string kitsRoot)
                {
                    var include = Path.Combine(kitsRoot, "Include");
                    if (Directory.Exists(include))
                        foreach (var version in Directory.EnumerateDirectories(include)
                            .OrderByDescending(x => x, StringComparer.Ordinal))
                            if (Directory.Exists(Path.Combine(version, "um"))
                                && Directory.Exists(Path.Combine(version, "ucrt"))
                                && File.Exists(Path.Combine(kitsRoot, "Lib", Path.GetFileName(version), "um", "x64", "kernel32.lib")))
                                Add("sdk", "windows-sdk", "windows-sdk", version, Path.GetFileName(version));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { inventory.Diagnostics.Add("Windows SDK registry lookup failed: " + ex.GetType().Name); }
        }
        // PATH may already contain cl.exe from a VS prompt. Preserve its actual
        // installation owner so a different VS environment cannot be selected.
        for (var i = 0; i < inventory.Candidates.Count; i++)
        {
            var candidate = inventory.Candidates[i];
            if (candidate.Role != "compiler" || candidate.VisualStudioInstanceId is not null) continue;
            var instance = inventory.VisualStudio.FirstOrDefault(v =>
                candidate.Path.StartsWith(v.InstallationPath.TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (instance is not null)
                inventory.Candidates[i] = candidate with
                {
                    VisualStudioInstanceId = instance.Id,
                    EnvironmentProfileId = instance.DeveloperCommandPath is null ? null : "vs:" + instance.Id
                };
        }
        inventory.Candidates.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return inventory;
    }

    public static ToolchainResolution Resolve(ToolchainInventory inventory, ToolchainSelection selection)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(selection);
        var messages = new List<string>();
        if (inventory.Platform != "windows") return new(1, "unsupported", null, null, null, null,
            null, ["Windows toolchain resolution is unavailable on this platform."]);
        if (string.IsNullOrWhiteSpace(selection.Generator))
            throw new ArgumentException("Generator is required.");
        var vsGenerator = selection.Generator.StartsWith("Visual Studio ", StringComparison.OrdinalIgnoreCase);
        if (selection.Generator is not ("Ninja" or "Ninja Multi-Config") && !vsGenerator)
            throw new ArgumentException("Only Ninja, Ninja Multi-Config and Visual Studio generators are supported by this resolver.");
        ToolchainCandidate? Pick(string role, string? id, bool required)
        {
            if (id is not null)
            {
                var found = inventory.Candidates.SingleOrDefault(x => x.Id == id && x.Role == role);
                if (found is null) throw new ArgumentException("Unknown " + role + " candidate ID; discover again.");
                return found;
            }
            var candidates = inventory.Candidates.Where(x => x.Role == role).ToArray();
            var best = candidates.OrderBy(x => x.Provider == "path" ? 0 : x.Provider == "standalone" ? 1 : 2)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (best is null && required) messages.Add("Missing " + role + " candidate.");
            if (best is not null && candidates.Length > 1)
                messages.Add("Selected " + role + " from " + best.Provider + "; alternatives are available.");
            return best;
        }
        var cmake = Pick("cmake", selection.CMakeCandidateId, true);
        var ninja = vsGenerator
            ? (selection.NinjaCandidateId is not null ? Pick("ninja", selection.NinjaCandidateId, false) : null)
            : Pick("ninja", selection.NinjaCandidateId, true);
        var compiler = Pick("compiler", selection.CompilerCandidateId, true);
        var sdk = Pick("sdk", selection.SdkCandidateId, false);
        var environment = selection.EnvironmentProfileId ?? compiler?.EnvironmentProfileId;
        if (vsGenerator && compiler?.Flavor is not ("msvc" or "clang-cl"))
            messages.Add("Visual Studio generator requires a compatible MSVC ABI toolchain; selection is unverified.");
        if (environment is not null)
        {
            var instanceId = environment.StartsWith("vs:", StringComparison.Ordinal)
                ? environment[3..] : "";
            if (!inventory.VisualStudio.Any(x => x.Id == instanceId && x.DeveloperCommandPath is not null))
                throw new ArgumentException("Unknown Visual Studio environment profile.");
            if (compiler?.VisualStudioInstanceId is { } owner && owner != instanceId)
                throw new ArgumentException("Compiler and developer environment belong to different Visual Studio instances.");
        }
        if (compiler?.Flavor is "msvc" or "clang-cl" && environment is null)
            messages.Add("MSVC ABI compiler requires a Visual Studio developer environment; select a matching profile.");
        if (compiler?.Flavor is "msvc" or "clang-cl" && sdk is null)
            messages.Add("MSVC ABI compiler requires a Windows SDK; none was detected.");
        if (compiler?.Flavor is "clang" or "gcc")
            messages.Add("Compiler ABI compatibility is unverified; the target CMake configuration must validate headers and linker.");
        var valid = cmake is not null && compiler is not null && (vsGenerator || ninja is not null)
            && (!vsGenerator || compiler?.Flavor is "msvc" or "clang-cl")
            && (compiler?.Flavor is not ("msvc" or "clang-cl") || environment is not null && sdk is not null);
        return new(1, valid ? "resolved" : "incomplete", cmake, ninja, compiler, sdk, environment, messages);
    }

    // No mutation of the Host's environment. Only these non-secret build configuration
    // variables are exposed, not the inherited environment block from cmd / set.
    private static readonly HashSet<string> Visible = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "INCLUDE", "LIB", "LIBPATH", "VCINSTALLDIR", "VCToolsInstallDir", "VCToolsVersion",
        "VSINSTALLDIR", "VisualStudioVersion", "WindowsSdkDir", "WindowsSDKVersion", "WindowsSdkVerBinPath",
        "UniversalCRTSdkDir", "UCRTVersion", "VSCMD_ARG_HOST_ARCH", "VSCMD_ARG_TGT_ARCH", "VSCMD_VER"
    };

    public static Dictionary<string, string> ParseVisibleEnvironment(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var index = line.IndexOf('=');
            if (index < 1) continue;
            var key = line[..index].Trim();
            if (Visible.Contains(key)) result[key] = line[(index + 1)..].TrimEnd('\r');
        }
        return result;
    }

    public static async Task<DeveloperEnvironmentSnapshot> InspectEnvironmentAsync(
        ToolchainInventory inventory, DeveloperEnvironmentRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("VsDevCmd is Windows-only.");
        if (request.HostArchitecture is not ("x86" or "x64" or "arm64") ||
            request.TargetArchitecture is not ("x86" or "x64" or "arm" or "arm64"))
            throw new ArgumentException("Unsupported host or target architecture.");
        if (request.WindowsSdkVersion is not null &&
            (request.WindowsSdkVersion.Length > 32 ||
             request.WindowsSdkVersion.Any(c => !char.IsAsciiDigit(c) && c != '.')))
            throw new ArgumentException("Invalid SDK version.");
        var instance = inventory.VisualStudio.SingleOrDefault(x => "vs:" + x.Id == request.EnvironmentProfileId)
            ?? throw new ArgumentException("Unknown developer environment profile.");
        var devcmd = instance.DeveloperCommandPath
            ?? throw new InvalidOperationException("Visual Studio instance has no VsDevCmd.bat.");
        if (devcmd.IndexOfAny(['"', '%', '\r', '\n']) >= 0)
            throw new InvalidDataException("Unsupported characters in Visual Studio installation path.");
        if (!File.Exists(devcmd)) throw new FileNotFoundException("Developer prompt script disappeared.", devcmd);
        var arguments = new StringBuilder();
        // Script path is exclusively obtained from verified local discovery; request values
        // are allowlisted above. cmd /s /c quotes keep spaces in installation paths.
        arguments.Append('"').Append('"').Append(devcmd).Append('"')
            .Append(" -no_logo -arch=").Append(request.TargetArchitecture)
            .Append(" -host_arch=").Append(request.HostArchitecture);
        if (request.WindowsSdkVersion is not null)
            arguments.Append(" -winsdk=").Append(request.WindowsSdkVersion);
        arguments.Append(" >nul && set").Append('"');
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(system))
            throw new InvalidOperationException("Windows system directory is unavailable.");
        var result = await RunBoundedAsync(Path.Combine(system, "cmd.exe"),
            ["/d", "/s", "/c", arguments.ToString()],
            TimeSpan.FromSeconds(20), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("VsDevCmd failed (exit " + result.ExitCode + ").");
        var vars = ParseVisibleEnvironment(result.Output);
        if (!vars.ContainsKey("VSCMD_VER") || !vars.ContainsKey("VCToolsInstallDir"))
            throw new InvalidDataException("VsDevCmd returned no initialized VC environment.");
        var canonical = string.Join("\n", vars.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key.ToUpperInvariant() + "=" + x.Value));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(1, request.EnvironmentProfileId, request.HostArchitecture,
            request.TargetArchitecture, fingerprint, vars,
            ["Preview only: workflow execution does not yet consume this environment snapshot."]);
    }

    private static async Task<ToolCommandResult> RunBoundedAsync(string executable,
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start discovery process.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var output = await stdout;
            var error = await stderr;
            if (output.Length > 2_000_000 || error.Length > 2_000_000)
                throw new InvalidDataException("Discovery output exceeds 2 MB.");
            return new(process.ExitCode, output, error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("Tool discovery timed out: " + executable);
        }
    }
}
