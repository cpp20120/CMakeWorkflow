namespace CMakeWorkflow.Tooling;

public sealed class ToolingService(IToolingRuntime runtime, ICMakeInstaller installer)
{
    private readonly IToolingRuntime runtime = runtime;
    private readonly ICMakeInstaller installer = installer;

    public async Task<ToolingPlan> PlanAsync(IReadOnlyList<string> requestedProfiles,
        PackageManagerKind requestedManager, bool allowFallback,
        string? installDirectory, CancellationToken ct)
    {
        var profiles = requestedProfiles.Count == 0 ? new[] { "cmake" }
            : requestedProfiles.Select(x => x.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        var directory = Path.GetFullPath(installDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        var manager = DetectManager(requestedManager, directory);
        var definitions = ToolCatalog.ResolveProfiles(profiles, runtime.Platform);
        var statuses = new List<ToolStatus>(definitions.Count);
        var steps = new List<ToolingStep>();
        var aptUpdate = false;
        foreach (var tool in definitions)
        {
            var status = await ProbeAsync(tool, directory, ct);
            statuses.Add(status);
            if (status.Installed) continue;
            if (tool.Packages.TryGetValue(manager, out var packages))
            {
                if (manager == PackageManagerKind.Apt && !aptUpdate)
                {
                    aptUpdate = true;
                    steps.Add(new("*", "package-index", "apt-get", ["update"], null, null));
                }
                var (command, args) = InstallCommand(manager, packages);
                steps.Add(new(tool.Id, "package", command, args, null, null));
            }
            if (allowFallback && tool.StandaloneCMake && runtime.Platform == ToolPlatform.Linux)
                steps.Add(new(tool.Id, "verified-release", null, [],
                    "https://github.com/Kitware/CMake/releases", "Requires a published SHA-256 asset digest."));
            if (!steps.Any(x => x.Tool == tool.Id))
                steps.Add(new(tool.Id, "manual", null, [], null,
                    tool.ManualHint ?? "No supported installer for this tool on this platform."));
        }
        return new(1, runtime.Platform, profiles, directory, manager,
            statuses.All(x => x.Installed), statuses, steps);
    }

    public async Task<ToolingResult> ExecuteAsync(ToolingPlan plan, CancellationToken ct)
    {
        var ready = new List<string>(); var failed = new List<string>(); var diagnostics = new List<string>();
        var aptUpdated = false;
        foreach (var status in plan.Tools)
        {
            ct.ThrowIfCancellationRequested();
            var definition = ToolCatalog.GetTool(status.Id);
            if ((await ProbeAsync(definition, plan.InstallDirectory, ct)).Installed)
            { ready.Add(status.Id); continue; }
            var packageStep = plan.Steps.FirstOrDefault(x => x.Tool == status.Id && x.Method == "package");
            if (packageStep is not null)
            {
                try
                {
                    if (plan.PackageManager == PackageManagerKind.Apt && !aptUpdated)
                    {
                        aptUpdated = true;
                        var update = await RunPackageAsync("apt-get", ["update"], plan.InstallDirectory, ct);
                        if (update.ExitCode != 0) throw new InvalidOperationException(
                            $"apt-get update failed: {update.Error.Trim()}");
                    }
                    var install = await RunPackageAsync(packageStep.Executable!, packageStep.Arguments,
                        plan.InstallDirectory, ct);
                    if (install.ExitCode != 0) diagnostics.Add($"{status.Id}: package installation exited {install.ExitCode}: {install.Error.Trim()}");
                    else if ((await ProbeAsync(definition, plan.InstallDirectory, ct)).Installed)
                    { ready.Add(status.Id); continue; }
                    else diagnostics.Add($"{status.Id}: package command succeeded but the required tool/version is still unavailable.");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { diagnostics.Add($"{status.Id}: package installation failed: {error.Message}"); }
            }
            if (plan.Steps.Any(x => x.Tool == status.Id && x.Method == "verified-release"))
            {
                try
                {
                    await installer.InstallAsync(plan.InstallDirectory, ct);
                    if ((await ProbeAsync(definition, plan.InstallDirectory, ct)).Installed)
                    { ready.Add(status.Id); continue; }
                    diagnostics.Add($"{status.Id}: verified CMake release installed, but probing still failed.");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { diagnostics.Add($"{status.Id}: verified CMake release failed: {error.Message}"); }
            }
            failed.Add(status.Id);
            if (definition.ManualHint is { } hint) diagnostics.Add($"{status.Id}: {hint}");
        }
        return new(1, failed.Count == 0, ready.ToArray(), failed.ToArray(), diagnostics.ToArray());
    }

    private async Task<ToolStatus> ProbeAsync(ToolDefinition tool, string directory, CancellationToken ct)
    {
        ToolStatus? outdated = null;
        foreach (var command in tool.Commands)
        {
            var resolved = runtime.Resolve(command, directory);
            if (resolved is null) continue;
            var args = command == "cl" ? new[] { "/?" } : tool.ProbeArguments;
            try
            {
                var result = await runtime.RunAsync(resolved, args, TimeSpan.FromSeconds(15), ct);
                if (result.ExitCode != 0 && command != "cl") continue;
                var version = ExtractVersion(result.Output + "\n" + result.Error);
                if (tool.MinimumVersion is null || version is not null && version >= tool.MinimumVersion)
                    return new(tool.Id, tool.DisplayName, true, resolved, version?.ToString(),
                        tool.MinimumVersion?.ToString(), null);
                outdated = new(tool.Id, tool.DisplayName, false, resolved, version?.ToString(),
                    tool.MinimumVersion.ToString(), version is null
                        ? "Version could not be determined." : $"Requires >= {tool.MinimumVersion}.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { outdated = new(tool.Id, tool.DisplayName, false, resolved, null,
                tool.MinimumVersion?.ToString(), error.Message); }
        }
        return outdated ?? new(tool.Id, tool.DisplayName, false, null, null,
            tool.MinimumVersion?.ToString(), "Command not found.");
    }

    public static Version? ExtractVersion(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i])) continue;
            var end = i; var dots = 0;
            while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'))
            { if (text[end] == '.') dots++; end++; }
            if (dots > 0 && Version.TryParse(text[i..end].TrimEnd('.'), out var version)) return version;
            i = end;
        }
        return null;
    }

    private PackageManagerKind DetectManager(PackageManagerKind requested, string directory)
    {
        if (requested == PackageManagerKind.Auto) return DetectAutoManager(directory);
        if (requested != PackageManagerKind.None)
        {
            var command = ManagerCommand(requested);
            if (command is null || runtime.Resolve(command, directory) is null)
                throw new ArgumentException($"Package manager {requested} is not available on this host.");
            if (requested == PackageManagerKind.Brew && runtime.Platform != ToolPlatform.MacOS
                || requested == PackageManagerKind.Winget && runtime.Platform != ToolPlatform.Windows
                || requested is (PackageManagerKind.Apt or PackageManagerKind.Pacman or PackageManagerKind.Dnf)
                    && runtime.Platform != ToolPlatform.Linux)
                throw new ArgumentException($"Package manager {requested} is not supported on {runtime.Platform}.");
            return requested;
        }
        return PackageManagerKind.None;
    }

    public PackageManagerKind DetectAutoManager(string directory)
    {
        var candidates = runtime.Platform switch
        {
            ToolPlatform.Linux => new[] { PackageManagerKind.Apt, PackageManagerKind.Pacman, PackageManagerKind.Dnf },
            ToolPlatform.MacOS => [PackageManagerKind.Brew],
            _ => [PackageManagerKind.Winget]
        };
        foreach (var candidate in candidates)
            if (runtime.Resolve(ManagerCommand(candidate)!, directory) is not null) return candidate;
        return PackageManagerKind.None;
    }

    public static (string Executable, string[] Arguments) InstallCommand(PackageManagerKind manager,
        string[] packages) => manager switch
    {
        PackageManagerKind.Apt => ("apt-get", ["install", "-y", .. packages]),
        PackageManagerKind.Pacman => ("pacman", ["-S", "--needed", "--noconfirm", .. packages]),
        PackageManagerKind.Dnf => ("dnf", ["install", "-y", .. packages]),
        PackageManagerKind.Brew => ("brew", ["install", .. packages]),
        PackageManagerKind.Winget => ("winget", ["install", "--id", packages[0], "--exact",
            "--accept-package-agreements", "--accept-source-agreements"]),
        _ => throw new ArgumentException("No package manager selected.")
    };

    private async Task<ToolCommandResult> RunPackageAsync(string executable, string[] args,
        string directory, CancellationToken ct)
    {
        if (runtime.Platform == ToolPlatform.Linux && !runtime.IsAdministrator)
        {
            var sudo = runtime.Resolve("sudo", directory)
                ?? throw new InvalidOperationException("sudo is required for package installation.");
            return await runtime.RunAsync(sudo, ["-n", executable, .. args], TimeSpan.FromMinutes(15), ct);
        }
        return await runtime.RunAsync(executable, args, TimeSpan.FromMinutes(15), ct);
    }

    private static string? ManagerCommand(PackageManagerKind manager) => manager switch
    {
        PackageManagerKind.Apt => "apt-get", PackageManagerKind.Pacman => "pacman",
        PackageManagerKind.Dnf => "dnf", PackageManagerKind.Brew => "brew",
        PackageManagerKind.Winget => "winget", _ => null
    };
}

public interface ICMakeInstaller
{
    Task InstallAsync(string installDirectory, CancellationToken ct);
}
