using System.Text.Json;
using CMakeWorkflow.Tooling.Tools;
using CMakeWorkflow.Tooling.VisualStudio;

namespace CMakeWorkflow.Tooling;

public static class ToolingCommand
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            if (args.Length == 0)
            {
                throw new ArgumentException(Usage);
            }

            if (args[0] == "vs")
            {
                return await VisualStudioSetupCommand.ExecuteAsync(args[1..], ct);
            }

            if (args[0] == "lock")
            {
                return await ManagedEnvironmentCommand.LockAsync(args[1..], ct);
            }

            if (args[0] == "vcpkg")
            {
                return await ManagedEnvironmentCommand.VcpkgAsync(args[1..], ct);
            }

            string action = args[0];
            if (action is not ("list" or "doctor" or "plan" or "setup"))
            {
                throw new ArgumentException(Usage);
            }

            List<string> profiles = [];
            PackageManagerKind manager = PackageManagerKind.Auto;
            bool fallback = true;
            ToolResolutionPolicy? policy = null;
            string? approval = null;
            bool json = false;
            bool dryRun = false;
            bool yes = false;
            string? installDirectory = null;
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--json": json = true; break;
                    case "--dry-run": dryRun = true; break;
                    case "--yes": yes = true; break;
                    case "--tool-policy" when i + 1 < args.Length:
                        policy = ParsePolicy(args[++i]); break;
                    case "--plan-sha" when i + 1 < args.Length:
                        approval = args[++i]; break;
                    case "--no-fallback": fallback = false; break;
                    case "--profile":
                        if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                        {
                            throw new ArgumentException("--profile requires a profile name.");
                        }

                        profiles.Add(args[++i]);
                        break;
                    case "--pm" or "--package-manager" when i + 1 < args.Length:
                        manager = ParseManager(args[++i]); break;
                    case "--install-dir" when i + 1 < args.Length:
                        installDirectory = args[++i]; break;
                    default:
                        if (args[i].StartsWith('-'))
                        {
                            throw new ArgumentException($"Unknown tooling option: {args[i]}");
                        }

                        profiles.Add(args[i]);
                        break;
                }
            }

            if (approval is not null && (policy is null || action != "setup"))
            {
                throw new ArgumentException("--plan-sha requires tooling setup with an explicit --tool-policy.");
            }

            if (action == "list")
            {
                if (profiles.Count > 0)
                {
                    throw new ArgumentException("tooling list does not accept profiles.");
                }

                ToolingProfileList listing = new(1, [.. ToolCatalog.AllProfiles.Select(x => new ToolingProfileView(x.Id, x.Description, x.Tools))]);
                if (json)
                {
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(listing,
                        ToolingJson.Default.ToolingProfileList));
                }
                else
                {
                    foreach (ToolingProfileView profile in listing.Profiles)
                    {
                        await Console.Out.WriteLineAsync(
                            $"{profile.Id,-12} {profile.Description}: {string.Join(", ", profile.Tools)}");
                    }
                }

                return 0;
            }

            ToolingService service = new(new SystemToolingRuntime(), new VerifiedCMakeInstaller());
            ToolingPlan plan = await service.PlanAsync(profiles, manager, fallback, installDirectory, ct, resolutionPolicy: policy);
            if (action is "doctor" or "plan" || dryRun)
            {
                PrintPlan(plan, json);
                if (policy is not null)
                {
                    await Console.Error.WriteLineAsync("Plan SHA-256: " + ToolingPlanApproval.Compute(plan));
                }

                return action == "doctor" && !plan.Ready ? 2 : 0;
            }

            if (plan.Ready)
            {
                ToolingResult complete = new(1, true,
                    [.. plan.Tools.Select(x => x.Id)], [], []);
                if (json)
                {
                    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(complete,
                        ToolingJson.Default.ToolingResult));
                }
                else
                {
                    await Console.Out.WriteLineAsync("All requested tools are already available.");
                }

                return 0;
            }

            if (!yes)
            {
                throw new ArgumentException(
                    "tooling setup changes the machine; inspect `tooling plan` and pass --yes to apply it.");
            }

            if (policy is not null && !string.Equals(approval, ToolingPlanApproval.Compute(plan),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Managed setup requires --plan-sha from the reviewed tooling plan.");
            }

            ToolingResult result = await service.ExecuteAsync(plan, ct);
            if (json)
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(result, ToolingJson.Default.ToolingResult));
            }
            else
            {
                foreach (string tool in result.Ready)
                {
                    await Console.Out.WriteLineAsync($"Ready: {tool}");
                }

                foreach (string failure in result.Failed)
                {
                    await Console.Out.WriteLineAsync($"Failed: {failure}");
                }

                foreach (string diagnostic in result.Diagnostics)
                {
                    await Console.Error.WriteLineAsync(diagnostic);
                }

                if (result.Success && policy is null && !PathContains(plan.InstallDirectory))
                {
                    await Console.Error.WriteLineAsync($"Add {plan.InstallDirectory} to PATH for future sessions.");
                }
            }

            return result.Success ? 0 : 2;
        }
        catch (Exception error) when (error is ArgumentException or IOException
                                          or UnauthorizedAccessException or InvalidDataException or HttpRequestException
                                          or TimeoutException or PlatformNotSupportedException or InvalidOperationException or JsonException)
        {
            await Console.Error.WriteLineAsync($"[CMakeWorkflow] Tooling: {error.Message}");
            return 1;
        }
    }

    private static void PrintPlan(ToolingPlan plan, bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(plan, ToolingJson.Default.ToolingPlan));
            return;
        }

        Console.Out.WriteLine(
            $"Profiles: {string.Join(", ", plan.Profiles)}; platform: {plan.Platform}; package manager: {plan.PackageManager}");
        foreach (ToolStatus tool in plan.Tools)
        {
            Console.Out.WriteLine(
                $"{tool.Resolution ?? (tool.Installed ? "ready" : "missing"),-12} {tool.Id,-12} {tool.Command ?? tool.Diagnostic ?? ""} {tool.Version ?? ""} {tool.Origin} {tool.Sha256}");
            if (tool.Diagnostic is not null && (tool.Installed || tool.Resolution is not null))
            {
                Console.Out.WriteLine($"  {tool.Id}: {tool.Diagnostic}");
            }
        }

        foreach (ToolingStep step in plan.Steps)
        {
            Console.Out.WriteLine(
                $"  {step.Tool}: {step.Method} {step.Executable} {string.Join(" ", step.Arguments)} {step.Source} {step.Release?.Version} {step.Release?.Sha256} {step.Diagnostic}");
        }
    }

    private static bool PathContains(string directory)
    {
        foreach (string path in (System.Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (string.Equals(Path.GetFullPath(path), directory,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
            }
        }

        return false;
    }


    private static readonly Dictionary<string, PackageManagerKind> PackageManagers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["auto"] = PackageManagerKind.Auto,
            ["none"] = PackageManagerKind.None,
            ["apt"] = PackageManagerKind.Apt,
            ["pacman"] = PackageManagerKind.Pacman,
            ["dnf"] = PackageManagerKind.Dnf,
            ["brew"] = PackageManagerKind.Brew,
            ["winget"] = PackageManagerKind.Winget
        };

    private static PackageManagerKind ParseManager(string value) =>
        PackageManagers.TryGetValue(value, out PackageManagerKind manager) ? manager
            : throw new ArgumentException("Package manager must be auto, none, apt, pacman, dnf, brew or winget.");


    public static ToolResolutionPolicy ParsePolicy(string value) => value?.ToUpperInvariant() switch
    {
        "ALLOWSYSTEM" => ToolResolutionPolicy.AllowSystem,
        "MANAGEDONLY" => ToolResolutionPolicy.ManagedOnly,
        _ => throw new ArgumentException("Tool policy must be AllowSystem or ManagedOnly.")
    };

    private const string Usage =
        "tooling <list|doctor|plan|setup|lock|vcpkg|vs> [profile...] [--profile NAME] [--json] [--pm auto|none|apt|pacman|dnf|brew|winget] [--install-dir PATH] [--no-fallback] [--tool-policy AllowSystem|ManagedOnly] [--plan-sha SHA] [--dry-run] [--yes]";
}
