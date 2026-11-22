using CMakeWorkflow.Contracts.Workflows;

#if EXPERIMENT_SDK
namespace CMakeWorkflow.Client.Experiments;
#else
namespace CMakeWorkflow.CMake.Recipes;
#endif

internal static class BenchmarkWorkflowNodes
{
    internal static void Add(List<WorkflowNode> nodes, string target, int repeats, IReadOnlyList<string> arguments,
        double timeoutSeconds, BenchmarkMetrics? metrics, bool managed)
    {
        nodes.Add(new()
        {
            Id = "build",
            Kind = "build",
            Context = "benchmark",
            Target = target,
            DependsOn = managed ? ["configure"] : []
        });
        for (int repeat = 1; repeat <= repeats; repeat++)
        {
            nodes.Add(new WorkflowNode
            {
                Id = $"timing_{repeat:D3}",
                Kind = "run",
                From = "build",
                DependsOn = [repeat == 1 ? "build" : $"timing_{repeat - 1:D3}"],
                Arguments = [.. arguments],
                Metrics = metrics,
                TimeoutSeconds = timeoutSeconds,
                Resources = ["measurement-host"]
            });
        }
    }
}
