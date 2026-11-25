using System.Globalization;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Scripting;

/// <summary>Composition helpers for existing native run and benchmark report semantics.</summary>
public static class WorkflowBenchmarks
{
    /// <summary>Creates serial timing runs from a shared build and returns their immutable references.</summary>
    /// <remarks>The returned nodes carry existing benchmark metadata and run exclusively by default.</remarks>
    public static IReadOnlyList<NodeRef> Benchmark(this Workflow workflow, TargetBuildRef build, string variant,
        int repeats, BenchmarkTrial trial, BenchmarkMetrics? metrics = null, Action<WorkflowNode>? options = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(trial);
        ArgumentException.ThrowIfNullOrWhiteSpace(variant);
        ArgumentOutOfRangeException.ThrowIfLessThan(repeats, 1);
        NodeRef[] timing = new NodeRef[repeats];
        for (int index = 0; index < timing.Length; index++)
        {
            string id = "timing_" + variant + "_" + (index + 1).ToString("D3", CultureInfo.InvariantCulture);
            timing[index] = workflow.Run(id, build, index == 0 ? [] : [timing[index - 1]], node =>
            {
                node.Exclusive = true;
                node.Resources.Add("measurement-host");
                options?.Invoke(node);
            }, metrics, trial);
        }
        return Array.AsReadOnly(timing);
    }
}
