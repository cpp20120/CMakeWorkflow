#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling;
#pragma warning restore IDE0130

public sealed record DagFlowHistoricalOptions(
    string SourceDirectory,
    string OutputDirectory,
    int TimingTrials = 5,
    int StatTrials = 3,
    int Jobs = 4,
    bool Resume = false);
