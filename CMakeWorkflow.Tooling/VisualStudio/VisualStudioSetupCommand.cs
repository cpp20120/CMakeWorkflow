using System.Text.Json;

namespace CMakeWorkflow.Tooling.VisualStudio;

public static class VisualStudioSetupCommand
{
    private const string Usage =
        "tooling vs <doctor|plan|setup> [--profile windows-cpp|windows-cpp-clang|windows-cpp-perf|windows-cpp-graphics] " +
        "[--instance ID] [--bootstrapper FULL_PATH --install-path FULL_PATH [--build-tools]] " +
        "[--json] [--yes --plan-sha SHA256]";

    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(args);
            if (args.Length == 0 || args[0] is not ("doctor" or "plan" or "setup"))
            {
                throw new ArgumentException(Usage);
            }

            string action = args[0];
            List<string> profiles = [];
            string? instance = null, bootstrapper = null, installPath = null, planSha = null;
            bool buildTools = false, json = false, yes = false;
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--profile" when i + 1 < args.Length: profiles.Add(args[++i]); break;
                    case "--instance" when i + 1 < args.Length: instance = args[++i]; break;
                    case "--bootstrapper" when i + 1 < args.Length: bootstrapper = args[++i]; break;
                    case "--install-path" when i + 1 < args.Length: installPath = args[++i]; break;
                    case "--plan-sha" when i + 1 < args.Length: planSha = args[++i]; break;
                    case "--build-tools": buildTools = true; break;
                    case "--json": json = true; break;
                    case "--yes": yes = true; break;
                    default: throw new ArgumentException($"Unknown Visual Studio option: {args[i]}");
                }
            }

            VisualStudioSetupRequest request = new(profiles, instance, bootstrapper, installPath, buildTools);
            VisualStudioSetupService service = new(new SystemVisualStudioSetupBackend());
            VisualStudioSetupPlan plan = await service.PlanAsync(request, ct);
            if (action is "doctor" or "plan")
            {
                Print(plan, json);
                return action == "doctor" && !plan.Ready ? 2 : 0;
            }

            if (!yes || string.IsNullOrWhiteSpace(planSha))
            {
                throw new ArgumentException("Installation requires --yes --plan-sha SHA256 from a reviewed tooling vs plan.");
            }

            VisualStudioSetupResult result = await service.ApplyAsync(request, planSha, ct);
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, VisualStudioSetupJson.Default.VisualStudioSetupResult));
            }
            else
            {
                Console.WriteLine(result.Ready ? "Visual Studio components verified." :
                    $"Visual Studio is not ready: {result.Diagnostic}");
            }

            return result.Ready ? 0 : 2;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException
                                          or UnauthorizedAccessException or InvalidOperationException
                                          or TimeoutException or System.ComponentModel.Win32Exception
                                          or JsonException or PlatformNotSupportedException)
        {
            await Console.Error.WriteLineAsync($"[CMakeWorkflow] Visual Studio: {error.Message}");
            return 1;
        }
    }

    private static void Print(VisualStudioSetupPlan plan, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(plan, VisualStudioSetupJson.Default.VisualStudioSetupPlan));
            return;
        }

        Console.WriteLine($"VS {plan.Mode}: {plan.InstallPath}");
        Console.WriteLine($"Profiles: {string.Join(", ", plan.Profiles)}");
        foreach (string component in plan.Components)
        {
            Console.WriteLine($"  {(plan.MissingComponents.Contains(component) ? "missing" : "ready"),-8} {component}");
        }

        if (!plan.Ready)
        {
            Console.WriteLine($"Installer: {plan.InstallerPath}");
            Console.WriteLine($"Arguments: {string.Join(" ", plan.Arguments)}");
            Console.WriteLine($"Approval SHA256: {plan.ApprovalSha256}");
        }

        if (plan.Diagnostic is not null)
        {
            Console.WriteLine(plan.Diagnostic);
        }
    }
}
