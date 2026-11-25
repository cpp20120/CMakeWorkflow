using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.Contracts.Kits;
using CMakeWorkflow.Developer;

namespace CMakeWorkflow.Commands;

internal static class KitCommand
{
    private const string Usage = "Usage: CMakeWorkflow kit <list|show KIT|current|use KIT|clear|plan KIT|lock KIT --plan-sha SHA256|verify KIT|doctor KIT> [--project DIRECTORY] [--json]";

    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args is ["--help"] or ["help"])
        {
            await Console.Out.WriteLineAsync(Usage);
            return 0;
        }
        if (args.Length == 0)
        {
            throw new ArgumentException(Usage);
        }

        string command = args[0];
        string? id = null;
        int start = 1;
        if (command is "show" or "use" or "plan" or "lock" or "verify" or "doctor")
        {
            if (args.Length < 2 || args[1].StartsWith('-'))
            {
                throw new ArgumentException(Usage);
            }
            id = args[1];
            start = 2;
        }
        else if (command is not ("list" or "current" or "clear"))
        {
            throw new ArgumentException(Usage);
        }

        Dictionary<string, string?> options = DeveloperCommand.Parse(args[start..],
            command == "lock" ? ["--project", "--json", "--plan-sha"] : ["--project", "--json"]);
        ProjectLocation location = new(Directory.GetCurrentDirectory()) { ProjectDirectory = options.GetValueOrDefault("--project") };
        bool json = options.ContainsKey("--json");
        if (command == "list")
        {
            string? project = await DeveloperLifecycle.FindKitProjectAsync(location, ct);
            IReadOnlyList<EngineeringKitDefinition> catalog = await DeveloperLifecycle.ListKitsAsync(project, ct);
            if (json)
            {
                await DeveloperCommand.WriteJsonAsync("inspected", writer =>
                {
                    writer.WritePropertyName("kits");
                    JsonSerializer.Serialize(writer, catalog, KitCliJson.Default.IReadOnlyListEngineeringKitDefinition);
                });
            }
            else
            {
                foreach (EngineeringKitDefinition item in catalog)
                {
                    await Console.Out.WriteLineAsync($"{item.Id}  {item.DisplayName}  {item.CMake.Generator}");
                }
            }
            return 0;
        }
        if (command == "show")
        {
            EngineeringKitDefinition kit = await DeveloperLifecycle.GetKitAsync(id!,
                await DeveloperLifecycle.FindKitProjectAsync(location, ct), ct);
            await DeveloperCommand.WriteJsonAsync("inspected", writer => WriteDefinition(writer, kit));
            return 0;
        }

        DiscoveredProject root = await DeveloperLifecycle.DiscoverProjectAsync(location, ct);
        switch (command)
        {
            case "use":
                await DeveloperLifecycle.SelectKitAsync(root.Directory, id!, ct);
                break;
            case "clear":
                await DeveloperLifecycle.ClearKitSelectionAsync(root.Directory, ct);
                break;
            case "current":
                break;
            case "doctor":
                ProjectInspection inspection = await DeveloperLifecycle.PlanRestoreAsync(new(location)
                {
                    Configuration = new() { Kit = id },
                    ProbeLocalTools = true
                }, ct);
                return await DeveloperCommand.WriteInspectionAsync(inspection, json, requireLocks: false);
            default:
                EngineeringKitPlan plan = command switch
                {
                    "lock" => await DeveloperLifecycle.LockKitAsync(root.Directory, id!,
                        options.GetValueOrDefault("--plan-sha") ?? throw new ArgumentException(Usage), ct),
                    "verify" => await DeveloperLifecycle.VerifyKitAsync(root.Directory, id!, ct),
                    _ => await DeveloperLifecycle.PlanKitAsync(root.Directory, id!, ct)
                };
                await DeveloperCommand.WriteJsonAsync(plan.Approved ? "inspected" : "planned", writer => WritePlan(writer, plan));
                return 0;
        }
        SelectedEngineeringKit? selected = await DeveloperLifecycle.GetSelectedKitAsync(root.Directory, ct: ct);
        if (json)
        {
            await DeveloperCommand.WriteJsonAsync("inspected", writer => writer.WriteString("kitId", selected?.Id));
        }
        else
        {
            await Console.Out.WriteLineAsync(selected?.Id ?? "No active Kit");
        }
        return 0;
    }

    internal static void WritePlan(Utf8JsonWriter writer, EngineeringKitPlan plan)
    {
        WriteDefinition(writer, plan.Definition);
        writer.WriteString("kitDefinitionSha256", plan.DefinitionSha256);
        writer.WriteString("kitLockSha256", plan.Lock.Sha256);
        writer.WriteBoolean("kitApproved", plan.Approved);
        writer.WriteString("environmentLockSha256", plan.Environment.EnvironmentLockSha256);
        writer.WriteString("environmentApprovalCommand", plan.EnvironmentApprovalCommand);
        writer.WritePropertyName("kitLock");
        writer.WriteRawValue(plan.Lock.Json.Span);
    }

    private static void WriteDefinition(Utf8JsonWriter writer, EngineeringKitDefinition definition)
    {
        writer.WritePropertyName("kit");
        JsonSerializer.Serialize(writer, definition, KitCliJson.Default.EngineeringKitDefinition);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(IReadOnlyList<EngineeringKitDefinition>))]
internal sealed partial class KitCliJson : JsonSerializerContext;
