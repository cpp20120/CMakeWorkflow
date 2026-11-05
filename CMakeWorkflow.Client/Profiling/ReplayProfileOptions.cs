namespace CMakeWorkflow.Client.Profiling;

public sealed record ReplayProfileOptions(
    string BaselineDirectory,
    string CandidateDirectory,
    string NodeId,
    string ResultsDirectory,
    string? CaseName = null,
    int HarnessIndex = 0,
    string Backend = "auto",
    double TimeoutSeconds = 300);
