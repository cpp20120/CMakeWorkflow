using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Scripting;

namespace CMakeWorkflow.Examples.NativeFragments;

public sealed record NativeBuildResult(ContextRef Context, NodeRef Configure, TargetBuildRef Build, NodeRef Test);

public sealed record NativeVariant(string Compiler, string Configuration)
{
    public string Preset => Compiler + "-" + Configuration;
}

public sealed record NativeBenchmarkResult(NativeBuildResult Build, IReadOnlyList<NodeRef> Trials);

public static class NativeWorkflowFragments
{
    public static NativeBuildResult ConfigureBuildTest(this WorkflowScope instance, NativeVariant variant,
        IEnumerable<NodeRef>? after = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(variant);
        Workflow workflow = instance.Workflow;
        ContextRef context = workflow.Context(instance.Id("context"), "build/" + variant.Preset,
            spec => spec.ConfigurePreset = variant.Preset);
        NodeRef configure = workflow.Configure(instance.Id("configure"), context, after);
        TargetBuildRef build = workflow.Build(instance.Id("build"), context, "worker", [configure]);
        NodeRef test = workflow.CTest(instance.Id("test"), context, [build]);
        return new(context, configure, build, test);
    }

    public static NativeBenchmarkResult BenchmarkWorker(this WorkflowScope instance, NativeVariant variant,
        string baseline, int repeats, IEnumerable<NodeRef>? after = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(variant);
        NativeBuildResult native = instance.Scope("native").ConfigureBuildTest(variant, after);
        BenchmarkTrial trial = new(baseline, "sequential",
            [new("compiler", "", variant.Compiler, null), new("configuration", "", variant.Configuration, null)]);
        IReadOnlyList<NodeRef> runs = instance.Workflow.Benchmark(native.Build, instance.InstanceId, repeats,
            trial, options: node => node.TimeoutSeconds = 30);
        return new(native, runs);
    }
}
