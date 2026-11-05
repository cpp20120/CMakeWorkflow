using System.Text.Json;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public static class WorkflowCommand
{
    public static async Task<int> ExecuteAsync(string command, string[] args, CancellationToken ct,
        IWorkflowOperationExecutor? extension = null, Action<string>? onRunDirectory = null,
        IWorkflowToolchainProvider? toolchainProvider = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            throw new ArgumentException("workflow plan/run requires a JSON file.");
        }

        List<string> only = [];
        int jobs = 1;
        string? results = null, controller = null;
        bool json = false;
        string? resumeDirectory = null;
        for (int i = 1; i < args.Length; ++i)
        {
            if (args[i] == "--json")
            {
                json = true;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for {args[i]}.");
            }

            switch (args[i])
            {
                case "--only": only.Add(args[++i]); break;
                case "--jobs":
                    if (!int.TryParse(args[++i], out jobs) || jobs <= 0)
                    {
                        throw new ArgumentException("--jobs must be a positive integer.");
                    }

                    break;
                case "--results-dir": results = args[++i]; break;
                case "--resume-dir": resumeDirectory = args[++i]; break;
                case "--controller-build-dir": controller = args[++i]; break;
                default: throw new ArgumentException($"Invalid workflow option: {args[i]}");
            }
        }

        if (resumeDirectory is not null && (command != "run" || only.Count != 0 || results is not null))
        {
            throw new ArgumentException("--resume-dir only supports workflow run without --only/--results-dir.");
        }

        WorkflowPlan plan = await WorkflowPlanner.LoadAsync(args[0], only, controller, ct);
        if (json && command == "plan")
        {
            WorkflowPlanView view = WorkflowQueries.PlanView(plan);
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(view, WorkflowProtocolJson.Default.WorkflowPlanView));
        }
        else
        {
            foreach (PlannedNode node in plan.Nodes)
            {
                await Console.Error.WriteLineAsync($"{node.Definition.Id}: {node.Definition.Kind} {node.Target.Target} " +
                                        $"[{node.Target.BuildDirectory}] requested-config={node.Requested.Configuration ?? "<unspecified>"} " +
                                        $"requested-preset={node.Requested.ConfigurePreset ?? "<unspecified>"} " +
                                        $"observed={node.Observed.State} configs=[{string.Join(", ", node.Observed.Configurations)}] " +
                                        $"after=[{string.Join(", ", node.Dependencies)}] resources=[{string.Join(", ", node.Definition.Resources)}]" +
                                        (node.Definition.Exclusive ? " exclusive" : ""));
                foreach (PlanDiagnostic diagnostic in node.Diagnostics)
                {
                    await Console.Error.WriteLineAsync($"  {diagnostic.Severity}: {diagnostic.Message}");
                }
            }
        }

        if (!json || command == "run")
        {
            await Console.Error.WriteLineAsync(
                "Scheduling: operations sharing a physical build tree or named resource conflict; exclusive nodes conflict with all nodes. Plain IDE/Ninja builds do not participate in tool locks.");
        }
        // Both plan and run must reject a known-invalid selected graph *before*
        // creating result files, acquiring leases or launching independent branches.
        // A configure predecessor can defer checks to runtime (Severity=Runtime).
        PlannedNode[] errors = [.. plan.Nodes.Where(n => n.Diagnostics.Any(d => d.Severity == "Error"))];
        if (errors.Length != 0)
        {
            if (command == "run")
            {
                await Console.Error.WriteLineAsync(
                    $"[CMakeWorkflow] Preflight rejected {errors.Length} node(s); no operations were launched.");
            }

            return 1;
        }

        if (command == "plan")
        {
            return 0;
        }

        results ??= Path.Combine(Path.GetDirectoryName(plan.File)!, "out", "workflow-runs");
        string output = resumeDirectory is null
            ? Path.Combine(Path.GetFullPath(results), $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}")
            : Path.GetFullPath(resumeDirectory);
        IReadOnlyDictionary<string, WorkflowToolchainRuntime> toolchains = await WorkflowToolchainAdmission.ResolveAsync(plan, toolchainProvider, ct);
        _ = Directory.CreateDirectory(output);
        onRunDirectory?.Invoke(output);
        await Console.Error.WriteLineAsync($"[CMakeWorkflow] Results: {output}");
        return await WorkflowRunner.RunAsync(plan, jobs, output, ct, extension, json, resumeDirectory is not null,
            toolchains: toolchains);
    }
}
