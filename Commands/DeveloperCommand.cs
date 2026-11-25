using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.Developer;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Commands;

internal static class DeveloperCommand
{
    private const string SelectionHelp = "[--project DIRECTORY] [--profile NAME|--matrix] [--variant ID] [--overrides FILE] [--json]";
    private const string ExecutionHelp = " [--plan] [--jobs N] [--results-dir DIRECTORY] [--history-db PATH|--no-history]";
    private static readonly string[] SelectionOptions = ["--project", "--profile", "--matrix", "--variant", "--overrides", "--json"];

    internal static async Task<int> ExecuteAsync(string command, string[] args, CancellationToken ct)
    {
        if (args is ["--help"] or ["help"])
        {
            await Console.Out.WriteLineAsync(Usage(command));
            return 0;
        }

        bool json = args.TakeWhile(argument => argument != "--").Contains("--json", StringComparer.Ordinal);
        try
        {
            return command == "new" ? await CreateAsync(args, ct) : await ProjectAsync(command, args, ct);
        }
        catch (Exception error) when (json && CliErrors.IsExpected(error))
        {
            await WriteJsonAsync("failed", writer => writer.WriteString("error", error.Message));
            return 1;
        }
    }

    private static async Task<int> CreateAsync(string[] args, CancellationToken ct)
    {
        if (args is not [string sdk, string name, .. string[] tail])
        {
            throw new ArgumentException(Usage("new"));
        }

        string directory = name;
        if (tail.Length > 0 && !tail[0].StartsWith('-'))
        {
            directory = tail[0];
            tail = tail[1..];
        }
        Dictionary<string, string?> values = Parse(tail, ["--sdk-version", "--environment-lock", "--json"]);
        CreatedProject project = await DeveloperLifecycle.CreateProjectAsync(new(sdk, name, directory)
        {
            SdkVersion = values.GetValueOrDefault("--sdk-version"),
            EnvironmentLockSha256 = values.GetValueOrDefault("--environment-lock")
        }, ct);
        if (values.ContainsKey("--json"))
        {
            await WriteJsonAsync("created", writer => writer.WriteString("projectDirectory", project.Directory));
        }
        else
        {
            await Console.Out.WriteLineAsync(project.Directory);
        }
        return 0;
    }

    private static async Task<int> ProjectAsync(string command, string[] args, CancellationToken ct)
    {
        string[] allowed = command switch
        {
            "restore" => [.. SelectionOptions, "--plan", "--sdk-plan-sha", "--configuration-plan-sha"],
            "doctor" or "inspect" => SelectionOptions,
            _ => [.. SelectionOptions, "--plan", "--jobs", "--results-dir", "--no-history", "--history-db", .. command switch
            {
                "test" => new[] { "--target", "--filter", "--label" },
                "build" or "run" => ["--target"],
                "benchmark" => ["--target", "--repeats"],
                "profile" => ["--target", "--backend", "--mode"],
                _ => []
            }]
        };
        int separator = Array.IndexOf(args, "--");
        if (separator >= 0 && command is not ("run" or "benchmark" or "profile"))
        {
            throw new ArgumentException(Usage(command));
        }
        Dictionary<string, string?> values = Parse(separator < 0 ? args : args[..separator], allowed);
        ProjectLocation location = new(Directory.GetCurrentDirectory()) { ProjectDirectory = values.GetValueOrDefault("--project") };
        ConfigurationSelection selection = new()
        {
            Profile = values.GetValueOrDefault("--profile"),
            Matrix = values.ContainsKey("--matrix"),
            Variant = values.GetValueOrDefault("--variant"),
            Overrides = values.GetValueOrDefault("--overrides")
        };
        bool plan = values.ContainsKey("--plan");
        bool json = values.ContainsKey("--json");
        if (command is "restore" or "inspect" or "doctor")
        {
            RestoreRequest restore = new(location)
            {
                Configuration = selection,
                ProbeLocalTools = command is "restore" or "doctor",
                SdkApprovalSha256 = values.GetValueOrDefault("--sdk-plan-sha"),
                ConfigurationApprovalSha256 = values.GetValueOrDefault("--configuration-plan-sha")
            };
            if (plan && (restore.SdkApprovalSha256 is not null || restore.ConfigurationApprovalSha256 is not null))
            {
                throw new ArgumentException("Review an approval plan before applying its digest.");
            }
            ProjectInspection inspection = command switch
            {
                "restore" when plan => await DeveloperLifecycle.PlanRestoreAsync(restore, ct),
                "restore" => await DeveloperLifecycle.RestoreAsync(restore, ct),
                "doctor" => await DeveloperLifecycle.PlanRestoreAsync(restore, ct),
                _ => await DeveloperLifecycle.InspectProjectAsync(location, selection, ct)
            };
            return await WriteInspectionAsync(inspection, json, command == "restore" && !plan);
        }

        DeveloperOperationRequest operation = new(location, Enum.Parse<DeveloperOperationKind>(command, ignoreCase: true))
        {
            Configuration = selection,
            Target = values.GetValueOrDefault("--target"),
            Arguments = separator < 0 ? [] : args[(separator + 1)..],
            TestRegex = values.GetValueOrDefault("--filter"),
            LabelRegex = values.GetValueOrDefault("--label"),
            Repeats = Positive(values, "--repeats", 5),
            ProfileBackend = values.GetValueOrDefault("--backend"),
            ProfileMode = values.GetValueOrDefault("--mode")
        };
        int jobs = Positive(values, "--jobs", 1);
        if (values.ContainsKey("--no-history") && values.ContainsKey("--history-db"))
        {
            throw new ArgumentException("Choose either --history-db or --no-history.");
        }
        DeveloperOperationPlan prepared = await DeveloperLifecycle.PlanOperationAsync(operation, ct);
        if (plan)
        {
            await WritePlanAsync(prepared, json);
            return 0;
        }
        return await RunAsync(operation, prepared, values, jobs, json, ct);
    }

    private static async Task<int> RunAsync(DeveloperOperationRequest operation, DeveloperOperationPlan plan,
        Dictionary<string, string?> values, int jobs, bool json, CancellationToken ct)
    {
        string requestId = Guid.NewGuid().ToString("N");
        HistoryPolicy history = values.ContainsKey("--no-history") ? new(HistoryPolicyMode.Disabled) :
            values.GetValueOrDefault("--history-db") is { } database ? new(HistoryPolicyMode.ExplicitDatabase, Path.GetFullPath(database)) :
            new(HistoryPolicyMode.Default);
        await using LocalDeveloperHost host = await LocalDeveloperHost.OpenAsync(plan.ProjectDirectory, ct);
        await Console.Error.WriteLineAsync("Request ID " + requestId);
        WorkflowOperation execution = await DeveloperLifecycle.ExecuteOperationAsync(host.Client, new(operation, plan.ManifestSha256, requestId)
        {
            Jobs = jobs,
            HistoryPolicy = history,
            ResultsDirectory = values.GetValueOrDefault("--results-dir")
        }, ct);
        await Console.Error.WriteLineAsync("Operation ID " + execution.OperationId);
        OperationOverview overview;
        try
        {
            overview = await host.WaitForOperationAsync(execution.OperationId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(20));
            _ = await execution.RequestCancellationAsync(0, cleanup.Token);
            overview = await host.WaitForOperationAsync(execution.OperationId, cleanup.Token);
        }
        HistoryIndexStatus historyStatus = await host.ObserveHistoryAsync(execution.OperationId,
            ct.IsCancellationRequested ? TimeSpan.Zero : TimeSpan.FromSeconds(10), CancellationToken.None);
        string status = overview.IsFinalized ? overview.Operation.State : "needs-attention";
        string? error = overview.IsFinalized ? overview.Error :
            $"Host has no finalized report. Operation state is {overview.Operation.State}. Inspect recovery for {execution.OperationId}.";
        if (json)
        {
            await WriteJsonAsync(status, writer =>
            {
                writer.WriteString("operationId", execution.OperationId);
                writer.WriteString("requestId", requestId);
                writer.WriteString("projectDirectory", plan.ProjectDirectory);
                writer.WriteString("manifestSha256", plan.ManifestSha256);
                writer.WriteString("configurationLockSha256", plan.ConfigurationLock.Sha256);
                writer.WriteString("runDirectory", overview.RunDirectory);
                writer.WriteString("reportSha256", overview.ReportSha256);
                writer.WriteString("error", error);
                writer.WritePropertyName("history");
                JsonSerializer.Serialize(writer, historyStatus, DeveloperCliJson.Default.HistoryIndexStatus);
                writer.WritePropertyName("nodes");
                JsonSerializer.Serialize(writer, overview.Nodes, DeveloperCliJson.Default.ListOperationNodeView);
            });
        }
        else
        {
            foreach (OperationNodeView node in overview.Nodes)
            {
                await Console.Out.WriteLineAsync(node.Id + " " + node.Status);
                if (node.Error is not null)
                {
                    await Console.Error.WriteLineAsync(node.Error + (node.Stderr is null ? "" : " (" + node.Stderr + ")"));
                }
            }
            if (error is not null)
            {
                await Console.Error.WriteLineAsync(error);
            }
            if (historyStatus.State != HistoryIndexState.Disabled)
            {
                await Console.Out.WriteLineAsync("History " + historyStatus.State +
                    (historyStatus.DiagnosticCode is null ? "" : " " + historyStatus.DiagnosticCode));
            }
            await Console.Out.WriteLineAsync(overview.RunDirectory);
        }
        return status switch { "succeeded" when historyStatus.State != HistoryIndexState.Failed => 0, "canceled" => 130, _ => 1 };
    }

    private static async Task<int> WriteInspectionAsync(ProjectInspection inspection, bool json, bool requireLocks)
    {
        bool approval = inspection.Diagnostics.Any(diagnostic => diagnostic.Code is DeveloperDiagnosticCode.SdkLockApprovalRequired
            or DeveloperDiagnosticCode.ConfigurationLockApprovalRequired or DeveloperDiagnosticCode.ConfigurationUnavailable
            or DeveloperDiagnosticCode.ToolingUnavailable or DeveloperDiagnosticCode.EnvironmentLockUnavailable
            or DeveloperDiagnosticCode.DependencyPlanApprovalRequired);
        if (json)
        {
            await WriteJsonAsync(approval ? "action-required" : "inspected", writer =>
            {
                writer.WriteString("projectDirectory", inspection.Project.Directory);
                writer.WriteString("sdk", inspection.Sdk.Id);
                writer.WriteString("sdkVersion", inspection.Sdk.Version);
                writer.WriteString("sdkLockSha256", inspection.SdkLock.Sha256);
                writer.WriteString("configurationLockSha256", inspection.Configuration?.Lock.Sha256);
                if (inspection.Configuration is { } configuration)
                {
                    writer.WritePropertyName("configuration");
                    writer.WriteRawValue(configuration.Lock.Json.Span);
                }
                writer.WriteString("vcpkgPlanSha256", inspection.VcpkgPlan?.Sha256);
                writer.WritePropertyName("diagnostics");
                JsonSerializer.Serialize(writer, inspection.Diagnostics, DeveloperCliJson.Default.IReadOnlyListDeveloperDiagnostic);
            });
        }
        else
        {
            await Console.Out.WriteLineAsync(inspection.Project.Directory);
            await Console.Out.WriteLineAsync(inspection.Sdk.Id + "/" + inspection.Sdk.Version);
            foreach (ProjectVariant variant in inspection.Configuration?.Variants ?? [])
            {
                await Console.Out.WriteLineAsync(variant.Name + " " + variant.Id + " " + variant.BuildDirectory);
            }
            foreach (ToolStatus tool in inspection.LocalTooling?.Tools ?? [])
            {
                await Console.Out.WriteLineAsync(tool.Id + " " + (tool.Installed ? "ready" : tool.Diagnostic ?? "unavailable"));
            }
            foreach (DeveloperDiagnostic diagnostic in inspection.Diagnostics)
            {
                await Console.Out.WriteLineAsync(diagnostic.Message + (diagnostic.Remediation is null ? "" : " " + diagnostic.Remediation));
            }
        }
        return approval && requireLocks ? 1 : 0;
    }

    private static Task WritePlanAsync(DeveloperOperationPlan plan, bool json) => json
        ? WriteJsonAsync("planned", writer =>
        {
            writer.WriteString("projectDirectory", plan.ProjectDirectory);
            writer.WriteString("sdk", plan.SdkId);
            writer.WriteString("sdkVersion", plan.SdkVersion);
            writer.WriteString("manifestName", plan.ManifestName);
            writer.WriteString("manifestSha256", plan.ManifestSha256);
            writer.WriteString("configurationLockSha256", plan.ConfigurationLock.Sha256);
            writer.WritePropertyName("manifest");
            writer.WriteRawValue(plan.ManifestJson.Span);
        })
        : Console.Out.WriteLineAsync(plan.ManifestSha256 + Environment.NewLine + Encoding.UTF8.GetString(plan.ManifestJson.Span));

    private static async Task WriteJsonAsync(string status, Action<Utf8JsonWriter> properties)
    {
        using MemoryStream output = new();
        using (Utf8JsonWriter writer = new(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("status", status);
            properties(writer);
            writer.WriteEndObject();
        }
        await Console.Out.WriteLineAsync(Encoding.UTF8.GetString(output.ToArray()));
    }

    private static int Positive(Dictionary<string, string?> values, string option, int fallback) =>
        !values.TryGetValue(option, out string? value) ? fallback :
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0 ? number :
        throw new ArgumentException(option + " must be a positive integer.");

    private static Dictionary<string, string?> Parse(string[] args, string[] allowed)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (!allowed.Contains(option, StringComparer.Ordinal) || !values.TryAdd(option, null))
            {
                throw new ArgumentException("Unexpected or duplicate option " + option);
            }
            if (option is "--json" or "--matrix" or "--plan" or "--no-history")
            {
                continue;
            }
            if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Missing value for " + option);
            }
            values[option] = args[i];
        }
        return values;
    }

    private static string Usage(string command) => "Usage: CMakeWorkflow " + command + (command switch
    {
        "new" => " SDK NAME [DIRECTORY] [--sdk-version VERSION] [--environment-lock SHA256] [--json]",
        "restore" => " " + SelectionHelp + " [--plan|--sdk-plan-sha DIGEST|--configuration-plan-sha DIGEST]",
        "doctor" or "inspect" => " " + SelectionHelp,
        _ => " " + SelectionHelp + ExecutionHelp + (command switch
        {
            "build" => " [--target NAME]",
            "test" => " [--target NAME] [--filter REGEX] [--label REGEX]",
            "run" => " [--target NAME] [-- PROGRAM_ARGS...]",
            "benchmark" => " [--target NAME] [--repeats N] [-- PROGRAM_ARGS...]",
            "profile" => " [--target NAME] [--backend auto|perf|etw|instruments] [--mode MODE] [-- PROGRAM_ARGS...]",
            _ => ""
        })
    });
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(IReadOnlyList<DeveloperDiagnostic>))]
[JsonSerializable(typeof(List<OperationNodeView>))]
[JsonSerializable(typeof(HistoryIndexStatus))]
internal sealed partial class DeveloperCliJson : JsonSerializerContext;
