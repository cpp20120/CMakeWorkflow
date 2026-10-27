using System.Text.Json;

namespace CMakeWorkflow.Tooling;

public static class ToolingCommand
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException(Usage);
            var action = args[0];
            if (action is not ("list" or "doctor" or "plan" or "setup"))
                throw new ArgumentException(Usage);
            var profiles = new List<string>();
            var manager = PackageManagerKind.Auto;
            var fallback = true; var json = false; var dryRun = false; var yes = false;
            string? installDirectory = null;
            for (var i = 1; i < args.Length; i++)
                switch (args[i])
                {
                    case "--json": json = true; break;
                    case "--dry-run": dryRun = true; break;
                    case "--yes": yes = true; break;
                    case "--no-fallback": fallback = false; break;
                    case "--profile":
                        if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException("--profile requires a profile name.");
                        profiles.Add(args[++i]); break;
                    case "--pm" or "--package-manager" when i + 1 < args.Length:
                        manager = ParseManager(args[++i]); break;
                    case "--install-dir" when i + 1 < args.Length:
                        installDirectory = args[++i]; break;
                    default:
                        if (args[i].StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException($"Unknown tooling option: {args[i]}");
                        profiles.Add(args[i]); break;
                }
            if (action == "list")
            {
                if (profiles.Count > 0) throw new ArgumentException("tooling list does not accept profiles.");
                var listing = new ToolingProfileList(1, ToolCatalog.AllProfiles
                    .Select(x => new ToolingProfileView(x.Id, x.Description, x.Tools)).ToList());
                if (json) Console.Out.WriteLine(JsonSerializer.Serialize(listing,
                    ToolingJson.Default.ToolingProfileList));
                else foreach (var profile in listing.Profiles)
                    Console.Out.WriteLine($"{profile.Id,-12} {profile.Description}: {string.Join(", ", profile.Tools)}");
                return 0;
            }
            var service = new ToolingService(new SystemToolingRuntime(), new VerifiedCMakeInstaller());
            var plan = await service.PlanAsync(profiles, manager, fallback, installDirectory, ct);
            if (action is "doctor" or "plan" || dryRun)
            {
                PrintPlan(plan, json);
                return action == "doctor" && !plan.Ready ? 2 : 0;
            }
            if (plan.Ready)
            {
                var complete = new ToolingResult(1, true,
                    plan.Tools.Select(x => x.Id).ToArray(), [], []);
                if (json) Console.Out.WriteLine(JsonSerializer.Serialize(complete,
                    ToolingJson.Default.ToolingResult));
                else Console.Out.WriteLine("All requested tools are already available.");
                return 0;
            }
            if (!yes) throw new ArgumentException("tooling setup changes the machine; inspect `tooling plan` and pass --yes to apply it.");
            var result = await service.ExecuteAsync(plan, ct);
            if (json) Console.Out.WriteLine(JsonSerializer.Serialize(result, ToolingJson.Default.ToolingResult));
            else
            {
                foreach (var tool in result.Ready) Console.Out.WriteLine($"Ready: {tool}");
                foreach (var failure in result.Failed) Console.Out.WriteLine($"Failed: {failure}");
                foreach (var diagnostic in result.Diagnostics) Console.Error.WriteLine(diagnostic);
                if (result.Success && !PathContains(plan.InstallDirectory))
                    Console.Error.WriteLine($"Add {plan.InstallDirectory} to PATH for future sessions.");
            }
            return result.Success ? 0 : 2;
        }
        catch (Exception error) when (error is ArgumentException or IOException
            or UnauthorizedAccessException or InvalidDataException or HttpRequestException
            or TimeoutException)
        {
            Console.Error.WriteLine($"[CMakeWorkflow] Tooling: {error.Message}");
            return 1;
        }
    }

    private static void PrintPlan(ToolingPlan plan, bool json)
    {
        if (json)
        { Console.Out.WriteLine(JsonSerializer.Serialize(plan, ToolingJson.Default.ToolingPlan)); return; }
        Console.Out.WriteLine($"Profiles: {string.Join(", ", plan.Profiles)}; platform: {plan.Platform}; package manager: {plan.PackageManager}");
        foreach (var tool in plan.Tools)
            Console.Out.WriteLine($"{(tool.Installed ? "ready" : "missing"),-7} {tool.Id,-12} {tool.Command ?? tool.Diagnostic ?? ""} {tool.Version ?? ""}");
        foreach (var step in plan.Steps)
            Console.Out.WriteLine($"  {step.Tool}: {step.Method} {step.Executable} {string.Join(" ", step.Arguments)} {step.Source} {step.Diagnostic}");
    }

    private static bool PathContains(string directory)
    {
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            try
            {
                if (string.Equals(Path.GetFullPath(path), directory,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal)) return true;
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException) { }
        return false;
    }

    private static PackageManagerKind ParseManager(string value) => value.ToLowerInvariant() switch
    {
        "auto" => PackageManagerKind.Auto, "none" => PackageManagerKind.None,
        "apt" => PackageManagerKind.Apt, "pacman" => PackageManagerKind.Pacman,
        "dnf" => PackageManagerKind.Dnf, "brew" => PackageManagerKind.Brew,
        "winget" => PackageManagerKind.Winget,
        _ => throw new ArgumentException("Package manager must be auto, none, apt, pacman, dnf, brew or winget.")
    };

    private const string Usage = "tooling <list|doctor|plan|setup> [profile...] [--profile NAME] [--json] [--pm auto|none|apt|pacman|dnf|brew|winget] [--install-dir PATH] [--no-fallback] [--dry-run] [--yes]";
}
