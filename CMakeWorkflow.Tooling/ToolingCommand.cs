using System.Text.Json;
using CMakeWorkflow.Tooling.Tools;

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

            string action = args[0];
            if (action is not ("list" or "doctor" or "plan" or "setup"))
            {
                throw new ArgumentException(Usage);
            }

            List<string> profiles = [];
            PackageManagerKind manager = PackageManagerKind.Auto;
            bool fallback = true;
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
            ToolingPlan plan = await service.PlanAsync(profiles, manager, fallback, installDirectory, ct);
            if (action is "doctor" or "plan" || dryRun)
            {
                PrintPlan(plan, json);
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

                if (result.Success && !PathContains(plan.InstallDirectory))
                {
                    await Console.Error.WriteLineAsync($"Add {plan.InstallDirectory} to PATH for future sessions.");
                }
            }

            return result.Success ? 0 : 2;
        }
        catch (Exception error) when (error is ArgumentException or IOException
                                          or UnauthorizedAccessException or InvalidDataException or HttpRequestException
                                          or TimeoutException)
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
                $"{(tool.Installed ? "ready" : "missing"),-7} {tool.Id,-12} {tool.Command ?? tool.Diagnostic ?? ""} {tool.Version ?? ""}");
            if (tool.Installed && tool.Diagnostic is not null)
            {
                Console.Out.WriteLine($"  {tool.Id}: {tool.Diagnostic}");
            }
        }

        foreach (ToolingStep step in plan.Steps)
        {
            Console.Out.WriteLine(
                $"  {step.Tool}: {step.Method} {step.Executable} {string.Join(" ", step.Arguments)} {step.Source} {step.Diagnostic}");
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


    private const string Usage =
        "tooling <list|doctor|plan|setup> [profile...] [--profile NAME] [--json] [--pm auto|none|apt|pacman|dnf|brew|winget] [--install-dir PATH] [--no-fallback] [--dry-run] [--yes]";
}
