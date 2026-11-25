using System.Text;
using System.Text.Json;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Developer;

namespace CMakeWorkflow.Commands;

internal static class ProjectConfigurationCommand
{
    private const string Usage = "project config <list|plan|lock|verify|workflow> DIRECTORY [--profile NAME|--matrix] [--overrides FILE] [--plan-sha SHA256] [--variant ID]";
    private sealed record Selection(string? Profile, bool Matrix, string? Overrides, string? Approval, string? Variant, bool Json);

    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args is not ["list" or "plan" or "lock" or "verify" or "workflow", string directory, .. string[] options])
        {
            throw new ArgumentException(Usage);
        }

        Selection selection = ParseOptions(options);
        string root = Path.GetFullPath(directory);
        if (args[0] == "list")
        {
            if (options.Length != 0 && options is not ["--json"])
            {
                throw new ArgumentException(Usage);
            }
            JsonElement profiles = await DeveloperLifecycle.ListConfigurationProfilesAsync(root, ct);
            await Console.Out.WriteLineAsync(profiles.GetRawText());
            return 0;
        }
        bool locking = args[0] == "lock";
        bool hasApproval = selection.Approval is not null;
        bool variantAllowed = selection.Variant is null || args[0] == "workflow";
        if (locking != hasApproval || !variantAllowed)
        {
            throw new ArgumentException(Usage);
        }

        ConfigurationSelection configuration = new() { Profile = selection.Profile, Matrix = selection.Matrix, Overrides = selection.Overrides };
        if (args[0] == "plan")
        {
            ConfigurationPlan plan = await DeveloperLifecycle.PlanConfigurationAsync(root, configuration, ct);
            await Console.Out.WriteLineAsync(Encoding.UTF8.GetString(plan.Lock.Json.Span));
            await Console.Error.WriteLineAsync("Configuration plan SHA-256: " + plan.Lock.Sha256);
            return 0;
        }

        ConfigurationPlan current = selection.Approval is { } approval
            ? await DeveloperLifecycle.LockConfigurationAsync(root, configuration, approval, ct)
            : await DeveloperLifecycle.VerifyConfigurationAsync(root, configuration, ct);
        string digest = current.Lock.Sha256;
        if (args[0] == "workflow")
        {
            WorkflowDocument workflow = await DeveloperLifecycle.CreateConfigurationWorkflowAsync(root, configuration, selection.Variant, ct);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(workflow, WorkflowJson.Default.WorkflowDocument));
        }
        else if (selection.Json)
        {
            using MemoryStream output = new();
            using (Utf8JsonWriter writer = new(output))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("configurationLockSha256", digest);
                writer.WriteString("path", current.Lock.Path);
                writer.WriteEndObject();
            }
            await Console.Out.WriteLineAsync(Encoding.UTF8.GetString(output.ToArray()));
        }
        else
        {
            await Console.Out.WriteLineAsync("Configuration lock SHA-256: " + digest);
        }

        return 0;
    }

    private static Selection ParseOptions(string[] args)
    {
        string? profile = null;
        string? overrides = null;
        string? approval = null;
        string? variant = null;
        bool matrix = false;
        bool json = false;
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (!seen.Add(option))
            {
                throw new ArgumentException(Usage);
            }
            switch (option)
            {
                case "--profile" when i + 1 < args.Length: profile = args[++i]; break;
                case "--overrides" when i + 1 < args.Length: overrides = args[++i]; break;
                case "--plan-sha" when i + 1 < args.Length: approval = args[++i]; break;
                case "--variant" when i + 1 < args.Length: variant = args[++i]; break;
                case "--matrix": matrix = true; break;
                case "--json": json = true; break;
                default: throw new ArgumentException(Usage);
            }
        }
        return matrix && profile is not null ? throw new ArgumentException(Usage) :
            new(profile, matrix, overrides, approval, variant, json);
    }

}
