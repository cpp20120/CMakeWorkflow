namespace CMakeWorkflow.Profiling.Analysis;

public sealed record EtwAnalysisResult(
    string EventDump,
    string Summary,
    string Samples,
    int CpuSamples,
    int ContextSwitchesIn,
    int ReadyEvents);
