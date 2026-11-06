using CMakeWorkflow.Tooling.Toolchains;

// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Tools;
#pragma warning restore IDE0130

public sealed class ToolingService(IToolingRuntime runtime, ICMakeInstaller installer)
{
    private readonly IToolingRuntime runtime = runtime;
    private readonly ICMakeInstaller installer = installer;

    public async Task<ToolingPlan> PlanAsync(IReadOnlyList<string> requestedProfiles,
        PackageManagerKind requestedManager, bool allowFallback,
        string? installDirectory, CancellationToken ct, Func<ToolStatus, Task>? onTool = null)
    {
        ArgumentNullException.ThrowIfNull(requestedProfiles);

#pragma warning disable CA1308 // Lowercase values are part of the existing persisted hash, identifier, or command protocol.
        string[] profiles = requestedProfiles.Count == 0
                    ? ["cmake"]
                    : [.. requestedProfiles.Select(x => x.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal)];
#pragma warning restore CA1308

        string directory = Path.GetFullPath(installDirectory ?? Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".local", "bin"));
        PackageManagerKind manager = DetectManager(requestedManager, directory);
        IReadOnlyList<ToolDefinition> definitions = ToolCatalog.ResolveProfiles(profiles, runtime.Platform);
        List<ToolStatus> statuses = new(definitions.Count);
        List<ToolingStep> steps = [];
        bool aptUpdate = false;
        foreach (ToolDefinition tool in definitions)
        {
            ToolStatus status = await ProbeAsync(tool, directory, ct);
            statuses.Add(status);
            if (onTool is not null)
            {
                await onTool(status);
            }

            if (status.Installed)
            {
                continue;
            }

            if (tool.Packages.TryGetValue(manager, out string[]? packages))
            {
                if (manager == PackageManagerKind.Apt && !aptUpdate)
                {
                    aptUpdate = true;
                    steps.Add(new("*", "package-index", "apt-get", ["update"], null, null));
                }

                (string? command, string[]? args) = InstallCommand(manager, packages);
                steps.Add(new(tool.Id, "package", command, args, null, null));
            }

            if (allowFallback && tool.StandaloneCMake && runtime.Platform == ToolPlatform.Linux)
            {
                steps.Add(new(tool.Id, "verified-release", null, [],
                    "https://github.com/Kitware/CMake/releases", "Requires a published SHA-256 asset digest."));
            }

            if (!steps.Any(x => x.Tool == tool.Id))
            {
                steps.Add(new(tool.Id, "manual", null, [], null,
                    tool.ManualHint ?? "No supported installer for this tool on this platform."));
            }
        }

        return new(1, runtime.Platform, profiles, directory, manager,
            statuses.All(x => x.Installed), statuses, steps);
    }

    public async Task<ToolingResult> ExecuteAsync(ToolingPlan plan, CancellationToken ct,
        Func<ToolingExecutionProgress, Task>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Task Progress(string tool, string stage, string? message = null) =>
            onProgress?.Invoke(new(tool, stage, message)) ?? Task.CompletedTask;

        List<string> ready = [];
        List<string> failed = [];
        List<string> diagnostics = [];
        bool aptUpdated = false;
        foreach (ToolStatus status in plan.Tools)
        {
            ct.ThrowIfCancellationRequested();
            ToolDefinition definition = ToolCatalog.GetTool(status.Id);
            if ((await ProbeAsync(definition, plan.InstallDirectory, ct)).Installed)
            {
                ready.Add(status.Id);
                await Progress(status.Id, "tool_ready");
                continue;
            }

            ToolingStep? packageStep = plan.Steps.FirstOrDefault(x => x.Tool == status.Id && x.Method == "package");
            if (packageStep is not null)
            {
                try
                {
                    if (plan.PackageManager == PackageManagerKind.Apt && !aptUpdated)
                    {
                        aptUpdated = true;
                        await Progress(status.Id, "package_index", "Updating package index");
                        ToolCommandResult update = await RunPackageAsync("apt-get", ["update"], plan.InstallDirectory, ct);
                        if (update.ExitCode != 0)
                        {
                            throw new InvalidOperationException(
                                $"apt-get update failed: {update.Error.Trim()}");
                        }
                    }

                    await Progress(status.Id, "package_install", "Installing through " + packageStep.Executable);
                    ToolCommandResult install = await RunPackageAsync(packageStep.Executable!, packageStep.Arguments,
                        plan.InstallDirectory, ct);
                    if (install.ExitCode != 0)
                    {
                        diagnostics.Add(
                            $"{status.Id}: package installation exited {install.ExitCode}: {install.Error.Trim()}");
                    }
                    else if ((await ProbeAsync(definition, plan.InstallDirectory, ct)).Installed)
                    {
                        ready.Add(status.Id);
                        await Progress(status.Id, "tool_ready");
                        continue;
                    }
                    else
                    {
                        diagnostics.Add(
                            $"{status.Id}: package command succeeded but the required tool/version is still unavailable.");
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    diagnostics.Add($"{status.Id}: package installation failed: {error.Message}");
                }
            }

            if (plan.Steps.Any(x => x.Tool == status.Id && x.Method == "verified-release"))
            {
                try
                {
                    await Progress(status.Id, "verified_release", "Installing verified CMake release");
                    await installer.InstallAsync(plan.InstallDirectory, ct);
                    if ((await ProbeAsync(definition, plan.InstallDirectory, ct)).Installed)
                    {
                        ready.Add(status.Id);
                        await Progress(status.Id, "tool_ready");
                        continue;
                    }

                    diagnostics.Add($"{status.Id}: verified CMake release installed, but probing still failed.");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    diagnostics.Add($"{status.Id}: verified CMake release failed: {error.Message}");
                }
            }

            failed.Add(status.Id);
            await Progress(status.Id, "tool_failed", definition.ManualHint);
            if (definition.ManualHint is { } hint)
            {
                diagnostics.Add($"{status.Id}: {hint}");
            }
        }

        return new(1, failed.Count == 0, [.. ready], [.. failed], [.. diagnostics]);
    }

    private async Task<ToolStatus> ProbeAsync(ToolDefinition tool, string directory, CancellationToken ct)
    {
        ToolStatus? outdated = null;
        // Windows desktop C++ uses the MSVC ABI toolchains. Never let a stray
        // MSYS2/MinGW c++.exe or g++.exe in PATH satisfy the default doctor probe.
        // GNU remains a normal default on Linux; an explicitly requested MinGW
        // workflow is a separate toolchain choice, not a fallback in doctor.
        string[] commands = tool.Id == "cxx" && runtime.Platform == ToolPlatform.Windows
            ? ["cl", "clang-cl", "clang++"]
            : tool.Commands;
        foreach (string? command in commands)
        {
            string? resolved = runtime.Resolve(command, directory);
            if (resolved is null)
            {
                continue;
            }

            string[] args = command == "cl" ? ["/?"] : tool.ProbeArguments;
            try
            {
                ToolCommandResult result = await runtime.RunAsync(resolved, args, TimeSpan.FromSeconds(15), ct);
                if (result.ExitCode != 0 && command != "cl")
                {
                    continue;
                }

                Version? version = ExtractVersion(result.Output + "\n" + result.Error);
                if (tool.MinimumVersion is null || (version is not null && version >= tool.MinimumVersion))
                {
                    string? diagnostic = null;
                    if (tool.Id == "perf" && runtime is SystemToolingRuntime
                                          && runtime.Platform == ToolPlatform.Linux)
                    {
                        string paranoid;
                        try
                        {
                            paranoid = (await File.ReadAllTextAsync("/proc/sys/kernel/perf_event_paranoid", ct)).Trim();
                        }
                        catch (IOException)
                        {
                            paranoid = "unavailable";
                        }
                        catch (UnauthorizedAccessException)
                        {
                            paranoid = "unavailable";
                        }

                        diagnostic = $"perf executable available; perf_event_paranoid={paranoid}. " +
                                     "PMU counters, sampling, user-space DWARF call chains and scheduler tracepoints " +
                                     "remain independently unverified. Run an explicit bounded profile capture; " +
                                     "scheduler mode may require additional tracepoint permissions.";
                    }

                    return new(tool.Id, tool.DisplayName, true, resolved, version?.ToString(),
                        tool.MinimumVersion?.ToString(), diagnostic);
                }

                outdated = new(tool.Id, tool.DisplayName, false, resolved, version?.ToString(),
                    tool.MinimumVersion.ToString(), version is null
                        ? "Version could not be determined."
                        : $"Requires >= {tool.MinimumVersion}.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                outdated = new(tool.Id, tool.DisplayName, false, resolved, null,
                    tool.MinimumVersion?.ToString(), error.Message);
            }
        }

        // A Visual Studio install does not imply cl.exe works in this process's
        // uninitialized PATH/INCLUDE/LIB. Report its presence without lying
        // about readiness or implicitly mutating the Host environment.
        if (outdated is null && tool.Id == "cxx" && runtime.Platform == ToolPlatform.Windows
            && runtime is SystemToolingRuntime)
        {
            ToolchainInventory inventory = await WindowsToolchains.DiscoverAsync(ct);
            ToolchainCandidate? msvc = inventory.Candidates.FirstOrDefault(x => x.Role == "compiler"
                                                                && x.Flavor == "msvc" &&
                                                                x.EnvironmentProfileId is not null);
            if (msvc is not null)
            {
                return new(tool.Id, tool.DisplayName, false, msvc.Path, msvc.Version,
                    tool.MinimumVersion?.ToString(),
                    "MSVC is installed but its Developer Command Prompt is not active. " +
                    "Use toolchains/resolve and toolchains/inspectEnvironment; execution environment binding is required.");
            }
        }

        return outdated ?? new(tool.Id, tool.DisplayName, false, null, null,
            tool.MinimumVersion?.ToString(), "Command not found.");
    }

    public static Version? ExtractVersion(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                continue;
            }

            int end = i;
            int dots = 0;
            while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'))
            {
                if (text[end] == '.')
                {
                    dots++;
                }

                end++;
            }

            if (dots > 0 && Version.TryParse(text[i..end].TrimEnd('.'), out Version? version))
            {
                return version;
            }

            i = end;
        }

        return null;
    }

    private PackageManagerKind DetectManager(PackageManagerKind requested, string directory)
    {
        if (requested == PackageManagerKind.Auto)
        {
            return DetectAutoManager(directory);
        }

        if (requested != PackageManagerKind.None)
        {
            string? command = ManagerCommand(requested);
            return command is null || runtime.Resolve(command, directory) is null
                ? throw new ArgumentException($"Package manager {requested} is not available on this host.")
                : (requested == PackageManagerKind.Brew && runtime.Platform != ToolPlatform.MacOS)
                || (requested == PackageManagerKind.Winget && runtime.Platform != ToolPlatform.Windows)
                || (requested is PackageManagerKind.Apt or PackageManagerKind.Pacman or PackageManagerKind.Dnf
                && runtime.Platform != ToolPlatform.Linux)
                ? throw new ArgumentException($"Package manager {requested} is not supported on {runtime.Platform}.")
                : requested;
        }

        return PackageManagerKind.None;
    }

    public PackageManagerKind DetectAutoManager(string directory)
    {
        PackageManagerKind[] candidates = runtime.Platform switch
        {
            ToolPlatform.Linux => [PackageManagerKind.Apt, PackageManagerKind.Pacman, PackageManagerKind.Dnf],
            ToolPlatform.MacOS => [PackageManagerKind.Brew],
            ToolPlatform.Windows => [PackageManagerKind.Winget],
            _ => [PackageManagerKind.Winget]
        };
        foreach (PackageManagerKind candidate in candidates)
        {
            if (runtime.Resolve(ManagerCommand(candidate)!, directory) is not null)
            {
                return candidate;
            }
        }

        return PackageManagerKind.None;
    }

    public static (string Executable, string[] Arguments) InstallCommand(PackageManagerKind manager,
        string[] packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        return manager switch
        {
            PackageManagerKind.Apt => ("apt-get", ["install", "-y", .. packages]),
            PackageManagerKind.Pacman => ("pacman", ["-S", "--needed", "--noconfirm", .. packages]),
            PackageManagerKind.Dnf => ("dnf", ["install", "-y", .. packages]),
            PackageManagerKind.Brew => ("brew", ["install", .. packages]),
            PackageManagerKind.Winget => ("winget", [
                "install", "--id", packages[0], "--exact",
            "--accept-package-agreements", "--accept-source-agreements"
            ]),
            PackageManagerKind.Auto => throw new ArgumentException("No package manager selected."),
            PackageManagerKind.None => throw new ArgumentException("No package manager selected."),
            _ => throw new ArgumentException("No package manager selected.")
        };
    }
    private async Task<ToolCommandResult> RunPackageAsync(string executable, string[] args,
        string directory, CancellationToken ct)
    {
        if (runtime.Platform == ToolPlatform.Linux && !runtime.IsAdministrator)
        {
            string sudo = runtime.Resolve("sudo", directory)
                       ?? throw new InvalidOperationException("sudo is required for package installation.");
            ToolCommandResult result = await runtime.RunAsync(sudo, ["-n", executable, .. args], TimeSpan.FromMinutes(15), ct);
            return result.ExitCode != 0
                ? (result with
                {
                    Error = result.Error +
                            "\nInstallation uses sudo -n and cannot prompt for a password. If authorization is required, install the reviewed packages in a terminal, then run Doctor again."
                })
                : result;
        }

        return await runtime.RunAsync(executable, args, TimeSpan.FromMinutes(15), ct);
    }

    private static string? ManagerCommand(PackageManagerKind manager) => manager switch
    {
        PackageManagerKind.Apt => "apt-get",
        PackageManagerKind.Pacman => "pacman",
        PackageManagerKind.Dnf => "dnf",
        PackageManagerKind.Brew => "brew",
        PackageManagerKind.Winget => "winget",
        PackageManagerKind.Auto => null,
        PackageManagerKind.None => null,
        _ => null
    };
}
