#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling;
#pragma warning restore IDE0130

public sealed record EtwAnalysisResult(
    string EventDump,
    string Summary,
    string Samples,
    int CpuSamples,
    int ContextSwitchesIn,
    int ReadyEvents);
