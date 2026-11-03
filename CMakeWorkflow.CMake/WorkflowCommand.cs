using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

public static class WorkflowCommand
{
    public static async Task<int> ExecuteAsync(string command, string[] args, CancellationToken ct,
        IWorkflowOperationExecutor? extension = null, Action<string>? onRunDirectory = null,
        IWorkflowToolchainProvider? toolchainProvider = null)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
            throw new ArgumentException("workflow plan/run requires a JSON file.");
        var only = new List<string>();
        var jobs = 1;
        string? results = null, controller = null;
        var json = false;
        string? resumeDirectory = null;
        for (var i = 1; i < args.Length; ++i)
        {
            if (args[i] == "--json")
            {
                json = true;
                continue;
            }

            if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
            switch (args[i])
            {
                case "--only": only.Add(args[++i]); break;
                case "--jobs":
                    if (!int.TryParse(args[++i], out jobs) || jobs <= 0)
                        throw new ArgumentException("--jobs must be a positive integer.");
                    break;
                case "--results-dir": results = args[++i]; break;
                case "--resume-dir": resumeDirectory = args[++i]; break;
                case "--controller-build-dir": controller = args[++i]; break;
                default: throw new ArgumentException($"Invalid workflow option: {args[i]}");
            }
        }

        if (resumeDirectory is not null && (command != "run" || only.Count != 0 || results is not null))
            throw new ArgumentException("--resume-dir only supports workflow run without --only/--results-dir.");
        var plan = await WorkflowPlanner.LoadAsync(args[0], only, controller, ct);
        if (json && command == "plan")
        {
            var view = WorkflowQueries.PlanView(plan);
            Console.Out.WriteLine(JsonSerializer.Serialize(view, WorkflowProtocolJson.Default.WorkflowPlanView));
        }
        else
            foreach (var node in plan.Nodes)
            {
                Console.Error.WriteLine($"{node.Definition.Id}: {node.Definition.Kind} {node.Target.Target} " +
                                        $"[{node.Target.BuildDirectory}] requested-config={node.Requested.Configuration ?? "<unspecified>"} " +
                                        $"requested-preset={node.Requested.ConfigurePreset ?? "<unspecified>"} " +
                                        $"observed={node.Observed.State} configs=[{string.Join(", ", node.Observed.Configurations)}] " +
                                        $"after=[{string.Join(", ", node.Dependencies)}] resources=[{string.Join(", ", node.Definition.Resources)}]" +
                                        (node.Definition.Exclusive ? " exclusive" : ""));
                foreach (var diagnostic in node.Diagnostics)
                    Console.Error.WriteLine($"  {diagnostic.Severity}: {diagnostic.Message}");
            }

        if (!json || command == "run")
            Console.Error.WriteLine(
                "Scheduling: operations sharing a physical build tree or named resource conflict; exclusive nodes conflict with all nodes. Plain IDE/Ninja builds do not participate in tool locks.");
        // Both plan and run must reject a known-invalid selected graph *before*
        // creating result files, acquiring leases or launching independent branches.
        // A configure predecessor can defer checks to runtime (Severity=Runtime).
        var errors = plan.Nodes.Where(n => n.Diagnostics.Any(d => d.Severity == "Error")).ToArray();
        if (errors.Length != 0)
        {
            if (command == "run")
                Console.Error.WriteLine(
                    $"[CMakeWorkflow] Preflight rejected {errors.Length} node(s); no operations were launched.");
            return 1;
        }

        if (command == "plan") return 0;

        results ??= Path.Combine(Path.GetDirectoryName(plan.File)!, "out", "workflow-runs");
        var output = resumeDirectory is null
            ? Path.Combine(Path.GetFullPath(results), $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}")
            : Path.GetFullPath(resumeDirectory);
        var toolchains = await WorkflowToolchainAdmission.ResolveAsync(plan, toolchainProvider, ct);
        Directory.CreateDirectory(output);
        onRunDirectory?.Invoke(output);
        Console.Error.WriteLine($"[CMakeWorkflow] Results: {output}");
        return await WorkflowRunner.RunAsync(plan, jobs, output, ct, extension, json, resumeDirectory is not null,
            toolchains: toolchains);
    }
}
