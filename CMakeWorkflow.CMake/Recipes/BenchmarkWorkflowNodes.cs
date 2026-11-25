using CMakeWorkflow.Contracts.Workflows;

#if EXPERIMENT_SDK
namespace CMakeWorkflow.Client.Experiments;
#else
namespace CMakeWorkflow.CMake.Recipes;
#endif

internal static class BenchmarkWorkflowNodes
{
    internal static void Add(List<WorkflowNode> nodes, string target, int repeats, IReadOnlyList<string> arguments,
        double timeoutSeconds, BenchmarkMetrics? metrics, bool managed, string context = "benchmark",
        string prefix = "", BenchmarkTrial? trial = null)
    {
        string build = prefix + "build";
        nodes.Add(new()
        {
            Id = build,
            Kind = "build",
            Context = context,
            Target = target,
            DependsOn = managed ? [prefix + "configure"] : []
        });
        for (int repeat = 1; repeat <= repeats; repeat++)
        {
            nodes.Add(new WorkflowNode
            {
                Id = $"{prefix}timing_{repeat:D3}",
                Kind = "run",
                From = build,
                DependsOn = [repeat == 1 ? build : $"{prefix}timing_{repeat - 1:D3}"],
                Arguments = [.. arguments],
                Metrics = metrics,
                BenchmarkTrial = trial,
                TimeoutSeconds = timeoutSeconds,
                Resources = ["measurement-host"]
            });
        }
    }
}
