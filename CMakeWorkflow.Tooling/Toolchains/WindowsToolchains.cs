using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Tooling.Tools;
using Microsoft.Win32;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Toolchains;
#pragma warning restore IDE0130

public static class WindowsToolchains
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    public static string CandidateId(string role, string path)
    {
        byte[] data = Encoding.UTF8.GetBytes(role + "\0" + Path.GetFullPath(path).ToUpperInvariant());

#pragma warning disable CA1308 // Lowercase values are part of the existing persisted hash, identifier, or command protocol.
        return Convert.ToHexString(SHA256.HashData(data))[..24].ToLowerInvariant();
#pragma warning restore CA1308

    }

    [SuppressMessage("Design", "CA1002", Justification = "Keep the established public collection type and source-generated JSON contract.")]
    public static List<VisualStudioInstance> ParseVsWhere(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("vswhere response must be an array.");
        }

        List<VisualStudioInstance> result = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            static string? Property(JsonElement item, string key) => item.TryGetProperty(key, out JsonElement value)
                                                                     && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

            string? id = Property(item, "instanceId");
            string? path = Property(item, "installationPath");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                continue;
            }

            string full = Path.GetFullPath(path);
            string devcmd = Path.Combine(full, "Common7", "Tools", "VsDevCmd.bat");
            result.Add(new(id, Property(item, "displayName") ?? id,
                Property(item, "installationVersion") ?? "unknown", full,
                File.Exists(devcmd) ? devcmd : null));
        }

        return [.. result.OrderBy(x => x.Id, StringComparer.Ordinal)];
    }

    public static async Task<ToolchainInventory> DiscoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ToolchainInventory inventory = new(1, OperatingSystem.IsWindows() ? "windows" : "unsupported",
            [], [], []);
        if (!OperatingSystem.IsWindows())
        {
            inventory.Diagnostics.Add("Windows toolchain discovery is only available on Windows.");
            return inventory;
        }

        HashSet<string> seen = new(Paths);

        void Add(string role, string flavor, string provider, string path, string? version = null,
            string? instanceId = null, string? environment = null)
        {
            if (!File.Exists(path) && !(role == "sdk" && Directory.Exists(path)))
            {
                return;
            }

            string full = Path.GetFullPath(path);
            string key = role + "\0" + full;
            if (!seen.Add(key) || inventory.Candidates.Count >= 512)
            {
                return;
            }

            inventory.Candidates.Add(new(CandidateId(role, full), role, flavor, provider, full,
                version, instanceId, environment));
        }

        (string Role, string Flavor, string Name)[] commandMap =
        [
            ("cmake", "cmake", "cmake.exe"), ("ninja", "ninja", "ninja.exe"),
            ("compiler", "clang-cl", "clang-cl.exe"), ("compiler", "clang", "clang++.exe"),
            ("compiler", "msvc", "cl.exe"),
            ("msbuild", "msbuild", "MSBuild.exe")
        ];
        string[] pathEntries = (System.Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string? folder in pathEntries.Distinct(Paths))
        {
            ct.ThrowIfCancellationRequested();
            foreach ((string? role, string? flavor, string? name) in commandMap)
            {
                try
                {
                    Add(role, flavor, "path", Path.Combine(folder.Trim('"'), name));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                }
            }
        }

        string[] roots = [.. new[]
            {
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86)
            }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(Paths)];
        foreach (string? root in roots)
        {
            Add("compiler", "clang", "standalone", Path.Combine(root, "LLVM", "bin", "clang++.exe"));
            Add("compiler", "clang-cl", "standalone", Path.Combine(root, "LLVM", "bin", "clang-cl.exe"));
            Add("cmake", "cmake", "standalone", Path.Combine(root, "CMake", "bin", "cmake.exe"));
        }

        IEnumerable<string> locators = roots.Select(root => Path.Combine(root, "Microsoft Visual Studio", "Installer", "vswhere.exe"))
            .Concat(pathEntries.Select(path => Path.Combine(path.Trim('"'), "vswhere.exe")))
            .Distinct(Paths);
        foreach (string? exe in locators)
        {
            if (!File.Exists(exe))
            {
                continue;
            }

            try
            {
                ToolCommandResult result = await RunBoundedAsync(exe,
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
                                              or InvalidDataException or System.ComponentModel.Win32Exception
                                              or TimeoutException)
            {
                inventory.Diagnostics.Add("vswhere discovery failed: " + error.GetType().Name);
            }
        }

        foreach (VisualStudioInstance vs in inventory.VisualStudio)
        {
            ct.ThrowIfCancellationRequested();
            string? profile = vs.DeveloperCommandPath is null ? null : "vs:" + vs.Id;
            string bundled = Path.Combine(vs.InstallationPath, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake");
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
            string vc = Path.Combine(vs.InstallationPath, "VC", "Tools", "MSVC");
            if (Directory.Exists(vc))
            {
                foreach (string? version in Directory.EnumerateDirectories(vc)
                             .OrderByDescending(x => x, StringComparer.Ordinal))
                {
                    Add("compiler", "msvc", "visual-studio", Path.Combine(version, "bin", "Hostx64", "x64", "cl.exe"),
                        Path.GetFileName(version), vs.Id, profile);
                }
            }

            string llvm = Path.Combine(vs.InstallationPath, "VC", "Tools", "Llvm", "bin");
            Add("compiler", "clang-cl", "visual-studio", Path.Combine(llvm, "clang-cl.exe"),
                instanceId: vs.Id, environment: profile);
            Add("compiler", "clang", "visual-studio", Path.Combine(llvm, "clang++.exe"),
                instanceId: vs.Id, environment: profile);
            string llvm64 = Path.Combine(vs.InstallationPath, "VC", "Tools", "Llvm", "x64", "bin");
            Add("compiler", "clang-cl", "visual-studio", Path.Combine(llvm64, "clang-cl.exe"),
                instanceId: vs.Id, environment: profile);
            Add("compiler", "clang", "visual-studio", Path.Combine(llvm64, "clang++.exe"),
                instanceId: vs.Id, environment: profile);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Kits\Installed Roots");
                if (key?.GetValue("KitsRoot10") is string kitsRoot)
                {
                    string include = Path.Combine(kitsRoot, "Include");
                    if (Directory.Exists(include))
                    {
                        foreach (string? version in Directory.EnumerateDirectories(include)
                                     .OrderByDescending(x => x, StringComparer.Ordinal))
                        {
                            if (Directory.Exists(Path.Combine(version, "um"))
                                && Directory.Exists(Path.Combine(version, "ucrt"))
                                && File.Exists(Path.Combine(kitsRoot, "Lib", Path.GetFileName(version), "um", "x64",
                                    "kernel32.lib")))
                            {
                                Add("sdk", "windows-sdk", "windows-sdk", version, Path.GetFileName(version));
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException)
            {
                inventory.Diagnostics.Add("Windows SDK registry lookup failed: " + ex.GetType().Name);
            }
        }

        // PATH may already contain cl.exe from a VS prompt. Preserve its actual
        // installation owner so a different VS environment cannot be selected.
        for (int i = 0; i < inventory.Candidates.Count; i++)
        {
            ToolchainCandidate candidate = inventory.Candidates[i];
            if (candidate.Role != "compiler" || candidate.VisualStudioInstanceId is not null)
            {
                continue;
            }

            VisualStudioInstance? instance = inventory.VisualStudio.FirstOrDefault(v =>
                candidate.Path.StartsWith(v.InstallationPath.TrimEnd(Path.DirectorySeparatorChar)
                                          + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (instance is not null)
            {
                inventory.Candidates[i] = candidate with
                {
                    VisualStudioInstanceId = instance.Id,
                    EnvironmentProfileId = instance.DeveloperCommandPath is null ? null : "vs:" + instance.Id
                };
            }
        }

        inventory.Candidates.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return inventory;
    }

    public static ToolchainResolution Resolve(ToolchainInventory inventory, ToolchainSelection selection)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(selection);
        List<string> messages = [];
        if (inventory.Platform != "windows")
        {
            return new(1, "unsupported", null, null, null, null,
                null, ["Windows toolchain resolution is unavailable on this platform."]);
        }

        if (string.IsNullOrWhiteSpace(selection.Generator))
        {
            throw new ArgumentException("Generator is required.");
        }

        bool vsGenerator = selection.Generator.StartsWith("Visual Studio ", StringComparison.OrdinalIgnoreCase);
        if (selection.Generator is not ("Ninja" or "Ninja Multi-Config") && !vsGenerator)
        {
            throw new ArgumentException(
                "Only Ninja, Ninja Multi-Config and Visual Studio generators are supported by this resolver.");
        }

        ToolchainCandidate? Pick(string role, string? id, bool required)
        {
            if (id is not null)
            {
                ToolchainCandidate? found = inventory.Candidates.SingleOrDefault(x => x.Id == id && x.Role == role);
                return found is null ? throw new ArgumentException("Unknown " + role + " candidate ID; discover again.") : found;
            }

            // Tool locations are not compatibility policy. For the Windows
            // compiler, prefer an MSVC-ABI toolset with a known VS environment
            // over an unrelated executable that happened to occur first in PATH.
            // In particular, GNU/MinGW is never an implicit selection.
            ToolchainCandidate[] candidates = [.. inventory.Candidates.Where(x => x.Role == role
                                                             && (role != "compiler" ||
                                                                 x.Flavor is "msvc" or "clang-cl" or "clang"))];
            ToolchainCandidate? best = candidates.OrderBy(x => role == "compiler"
                    ? (x.EnvironmentProfileId is null ? 10 : 0) + (x.Flavor switch
                    {
                        "msvc" => 0,
                        "clang-cl" => 1,
                        "clang" => 2,
                        _ => 9
                    })
                    : x.Provider == "path"
                        ? 0
                        : x.Provider == "standalone"
                            ? 1
                            : 2)
                .ThenBy(x => x.Provider == "visual-studio" ? 0 : x.Provider == "standalone" ? 1 : 2)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (best is null && required)
            {
                messages.Add("Missing " + role + " candidate.");
            }

            if (best is not null && candidates.Length > 1)
            {
                messages.Add("Selected " + role + " from " + best.Provider + "; alternatives are available.");
            }

            return best;
        }

        ToolchainCandidate? cmake = Pick("cmake", selection.CMakeCandidateId, true);
        ToolchainCandidate? ninja = vsGenerator
            ? (selection.NinjaCandidateId is not null ? Pick("ninja", selection.NinjaCandidateId, false) : null)
            : Pick("ninja", selection.NinjaCandidateId, true);
        ToolchainCandidate? compiler = Pick("compiler", selection.CompilerCandidateId, true);
        // A manually selected GNU toolchain is a distinct ABI environment:
        // don't silently bind the Windows SDK or a VS developer prompt to it.
        ToolchainCandidate? sdk = compiler?.Flavor == "gcc" && selection.SdkCandidateId is null
            ? null
            : Pick("sdk", selection.SdkCandidateId, false);
        string? environment = selection.EnvironmentProfileId ?? compiler?.EnvironmentProfileId;
        if (compiler?.Flavor == "gcc" && (environment is not null || selection.SdkCandidateId is not null))
        {
            throw new ArgumentException(
                "GNU/MinGW cannot be combined with a Visual Studio environment or Windows SDK in this resolver.");
        }

        if (vsGenerator && compiler?.Flavor is not ("msvc" or "clang-cl"))
        {
            messages.Add("Visual Studio generator requires a compatible MSVC ABI toolchain; selection is unverified.");
        }

        if (environment is not null)
        {
            string instanceId = environment.StartsWith("vs:", StringComparison.Ordinal)
                ? environment[3..]
                : "";
            if (!inventory.VisualStudio.Any(x => x.Id == instanceId && x.DeveloperCommandPath is not null))
            {
                throw new ArgumentException("Unknown Visual Studio environment profile.");
            }

            if (compiler?.VisualStudioInstanceId is { } owner && owner != instanceId)
            {
                throw new ArgumentException(
                    "Compiler and developer environment belong to different Visual Studio instances.");
            }
        }

        if ((compiler?.Flavor is "msvc" or "clang-cl" or "clang") && environment is null)
        {
            messages.Add(
                "Windows MSVC-ABI compiler requires a Visual Studio developer environment; select a matching profile.");
        }

        if ((compiler?.Flavor is "msvc" or "clang-cl" or "clang") && sdk is null)
        {
            messages.Add("Windows MSVC-ABI compiler requires a Windows SDK; none was detected.");
        }

        if (compiler?.Flavor == "clang")
        {
            messages.Add(
                "clang++ target triple must be checked by CMake; this resolver assumes an explicitly activated MSVC environment.");
        }

        if (compiler?.Flavor == "gcc")
        {
            messages.Add(
                "Explicit GNU/MinGW toolchain selected; no VS developer environment or Windows SDK is assumed.");
        }

        bool valid = cmake is not null && compiler is not null && (vsGenerator || ninja is not null)
                    && (!vsGenerator || compiler.Flavor is "msvc" or "clang-cl")
                    && (compiler.Flavor is not ("msvc" or "clang-cl" or "clang") ||
                        (environment is not null && sdk is not null));
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
        ArgumentNullException.ThrowIfNull(output);
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split('\n'))
        {
            int index = line.IndexOf('=', StringComparison.Ordinal);
            if (index < 1)
            {
                continue;
            }

            string key = line[..index].Trim();
            if (Visible.Contains(key))
            {
                result[key] = line[(index + 1)..].TrimEnd('\r');
            }
        }

        return result;
    }

    public static async Task<DeveloperEnvironmentSnapshot> InspectEnvironmentAsync(
        ToolchainInventory inventory, DeveloperEnvironmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("VsDevCmd is Windows-only.");
        }

        if (request.HostArchitecture is not ("x86" or "x64" or "arm64") ||
            request.TargetArchitecture is not ("x86" or "x64" or "arm" or "arm64"))
        {
            throw new ArgumentException("Unsupported host or target architecture.");
        }

        if (request.WindowsSdkVersion is not null &&
            (request.WindowsSdkVersion.Length > 32 ||
             request.WindowsSdkVersion.Any(c => !char.IsAsciiDigit(c) && c != '.')))
        {
            throw new ArgumentException("Invalid SDK version.");
        }

        if (request.ToolsetVersion is not null &&
            (request.ToolsetVersion.Length > 32 || request.ToolsetVersion.Any(c => !char.IsAsciiDigit(c) && c != '.')))
        {
            throw new ArgumentException("Invalid MSVC toolset version.");
        }

        VisualStudioInstance instance = inventory.VisualStudio.SingleOrDefault(x => "vs:" + x.Id == request.EnvironmentProfileId)
                       ?? throw new ArgumentException("Unknown developer environment profile.");
        string devcmd = instance.DeveloperCommandPath
                     ?? throw new InvalidOperationException("Visual Studio instance has no VsDevCmd.bat.");
        if (!File.Exists(devcmd))
        {
            throw new FileNotFoundException("Developer prompt script disappeared.", devcmd);
        }

        string system = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(system))
        {
            throw new InvalidOperationException("Windows system directory is unavailable.");
        }

        // cmd /s /c accepts an entire COMMAND LINE, not one argv element.
        // ArgumentList's CreateProcess quoting is not cmd.exe's quoting language:
        // it can alter the nested quotes around a path such as "Program Files".
        // Supply the raw shell command through Arguments, and CALL the batch file
        // so control returns to `&& set` after successful VsDevCmd activation.
        ProcessStartInfo start = new(Path.Combine(system, "cmd.exe"))
        {
            Arguments = DeveloperCommandLine(devcmd, request)
        };
        ToolCommandResult result = await RunBoundedAsync(start, TimeSpan.FromSeconds(20), ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("VsDevCmd failed (exit " + result.ExitCode + "). " +
                                                DeveloperCommandFailureDetails(result.Error));
        }

        Dictionary<string, string> vars = ParseVisibleEnvironment(result.Output);
        if (!vars.ContainsKey("VSCMD_VER") || !vars.ContainsKey("VCToolsInstallDir"))
        {
            throw new InvalidDataException("VsDevCmd returned no initialized VC environment.");
        }

        string canonical = string.Join("\n", vars.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Key.ToUpperInvariant() + "=" + x.Value));
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(1, request.EnvironmentProfileId, request.HostArchitecture,
            request.TargetArchitecture, fingerprint, vars,
            ["Developer environment preview. Workflow context opt-in must still bind a selected toolchain."]);
    }

    private static string DeveloperCommandLine(string devcmd, DeveloperEnvironmentRequest request)
    {
        // VS installation paths come from local discovery, but are still passed
        // through cmd's parser. Avoid metacharacter/variable expansion, including
        // delayed expansion if it was enabled by the caller's environment.
        if (devcmd.IndexOfAny(['"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n']) >= 0)
        {
            throw new InvalidDataException("Unsafe characters in Visual Studio developer command path.");
        }

        StringBuilder command = new StringBuilder("call \"").Append(devcmd).Append('"')
            .Append(" -no_logo -arch=").Append(request.TargetArchitecture)
            .Append(" -host_arch=").Append(request.HostArchitecture);
        if (request.WindowsSdkVersion is not null)
        {
            _ = command.Append(" -winsdk=").Append(request.WindowsSdkVersion);
        }

        if (request.ToolsetVersion is not null)
        {
            _ = command.Append(" -vcvars_ver=").Append(request.ToolsetVersion);
        }

        // The batch script's normal output and errors go only to stderr.
        // stdout is reserved for the environment snapshot emitted by `set`.
        // Never include stdout in an exception: it may contain credentials.
        _ = command.Append(" 1>&2 && set");
        return "/d /v:off /s /c \"" + command + "\"";
    }

    private static string DeveloperCommandFailureDetails(string stderr)
    {
        // Do not return arbitrary debug output or the `set` environment block
        // through Host RPC. Keep only characteristic diagnostic lines.
        IEnumerable<string> lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase)
                           || line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("is not recognized", StringComparison.OrdinalIgnoreCase)
                           || line.Contains("The system cannot find", StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .Select(line => line.Length > 240 ? line[..240] + "..." : line);
        string errors = string.Join(" | ", lines);
        return errors.Length == 0 ? "No diagnostic error lines were emitted." : errors;
    }

    private static async Task<ToolCommandResult> RunBoundedAsync(string executable,
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        ProcessStartInfo info = new(executable);
        foreach (string arg in arguments)
        {
            info.ArgumentList.Add(arg);
        }

        return await RunBoundedAsync(info, timeout, ct);
    }

    private static async Task<ToolCommandResult> RunBoundedAsync(ProcessStartInfo info,
        TimeSpan timeout, CancellationToken ct)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using Process process = Process.Start(info) ?? throw new IOException("Cannot start discovery process.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            string output = await stdout;
            string error = await stderr;
            return output.Length > 2_000_000 || error.Length > 2_000_000
                ? throw new InvalidDataException("Discovery output exceeds 2 MB.")
                : new(process.ExitCode, output, error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("Tool discovery timed out: " + info.FileName);
        }
    }
}