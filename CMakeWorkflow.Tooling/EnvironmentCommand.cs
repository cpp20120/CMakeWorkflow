using System.Text.Json;

namespace CMakeWorkflow.Tooling;

public static class EnvironmentCommand
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            if (args.Length >= 2 && args[0] == "probe-etw")
            {
                if (args.Length is not (2 or 3) || (args.Length == 3 && args[2] != "--json"))
                {
                    throw new ArgumentException("env probe-etw <user|kernel.cpu|kernel.scheduler> [--json]");
                }

                string capability = args[1];
                if (capability is not ("user" or "kernel.cpu" or "kernel.scheduler"))
                {
                    throw new ArgumentException("Unknown ETW capability.");
                }

                EtwProbeResult probe = new NativeWindowsEtwProbe().Probe(capability, ct);
                if (args.Length == 3)
                {
                    Console.WriteLine(JsonSerializer.Serialize(probe, HostPreparationJson.Default.EtwProbeResult));
                }
                else
                {
                    Console.WriteLine($"ETW {probe.Capability}: {probe.Status} ({probe.Diagnostic})");
                }

                return probe.Status == "available" ? 0 : 2;
            }

            if (args.Length < 2 || args[0] is not ("doctor" or "plan" or "setup"))
            {
                throw new ArgumentException("env <doctor|plan|setup> <requirements.json> [--yes] [--json]");
            }

            string action = args[0];
            string source = args[1];
            bool yes = false;
            bool json = false;
            bool allowSystemChanges = false;
            string? expectedPlan = null;
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--yes": yes = true; break;
                    case "--json": json = true; break;
                    case "--allow-system-changes": allowSystemChanges = true; break;
                    case "--plan-sha" when i + 1 < args.Length:
                        expectedPlan = args[++i]; break;
                    default: throw new ArgumentException($"Unknown environment option: {args[i]}");
                }
            }

            ToolingService service = new(new SystemToolingRuntime(), new VerifiedCMakeInstaller());
            HostPreparationPlan plan = await HostPreparation.InspectAsync(source, service, PackageManagerKind.Auto, null, ct);
            if (action != "setup")
            {
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(plan, HostPreparationJson.Default.HostPreparationPlan));
                }
                else
                {
                    Print(plan);
                }

                return action == "doctor" && !plan.Ready ? 2 : 0;
            }

            if (!yes || expectedPlan is null ||
                !string.Equals(expectedPlan, plan.ApprovalSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Review env plan and pass --yes --plan-sha <approvalSha256> for that exact plan.");
            }

            if (!json)
            {
                Print(plan);
            }

            HostPreparationResult result = await HostPreparation.ApplyAsync(plan, service, PackageManagerKind.Auto, null, ct,
                allowSystemChanges);
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, HostPreparationJson.Default.HostPreparationResult));
            }
            else
            {
                Console.WriteLine(result.Ready ? "Environment ready." : "Environment still has drift.");
                Print(result.Checked);
            }

            return result.Ready ? 0 : 2;
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or InvalidOperationException
                                      or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            await Console.Error.WriteLineAsync("[CMakeWorkflow] Environment: " + e.Message);
            return 1;
        }
    }

    private static void Print(HostPreparationPlan plan)
    {
        Console.WriteLine(
            $"Requirements: {plan.Source}\nSHA256: {plan.RequirementsSha256}\nApproval SHA256: {plan.ApprovalSha256}");
        foreach (ToolStatus tool in plan.Tools.Tools)
        {
            Console.WriteLine($"Tool {tool.Id}: {(tool.Installed ? "ready" : "missing")}");
        }

        foreach (ToolingStep step in plan.Tools.Steps)
        {
            Console.WriteLine($"Plan: {step.Tool} {step.Method} {step.Executable} {string.Join(" ", step.Arguments)}");
        }

        foreach (HostSettingChange change in plan.SystemChanges ?? [])
        {
            Console.WriteLine($"System plan: {change.Id} {change.Path}: {change.Before} -> {change.After}");
        }

        foreach (HostFinding finding in plan.Findings)
        {
            Console.WriteLine(
                $"Host {finding.Id}: {finding.Status} ({finding.Observed ?? "unavailable"}; expected {finding.Expected}) — {finding.Diagnostic}");
        }
    }
}
