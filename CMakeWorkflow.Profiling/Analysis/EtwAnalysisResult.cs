using CMakeWorkflow.Core;

namespace CMakeWorkflow.Profiling.Analysis;

public sealed record EtwAnalysisResult(
    string EventDump,
    string Summary,
    string Samples,
    int CpuSamples,
    int ContextSwitchesIn,
    int ReadyEvents)
{
    internal WorkflowProfileAnalysis? Analysis { get; init; }
}
