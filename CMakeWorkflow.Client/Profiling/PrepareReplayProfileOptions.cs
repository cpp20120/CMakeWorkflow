namespace CMakeWorkflow.Client.Profiling;

/// <summary>Prepare a checked immutable replay DAG, without starting profilers.</summary>
public sealed record PrepareReplayProfileOptions(
    string RequestId,
    string ResultsDirectory,
    string BaselineDirectory,
    string CandidateDirectory,
    string NodeId,
    string? CaseName = null,
    int HarnessIndex = 0,
    string Backend = "auto",
    double TimeoutSeconds = 300);
