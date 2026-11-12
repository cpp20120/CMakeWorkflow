namespace CMakeWorkflow.Host.Profiling;

// Preparation does not start execution. Start the returned immutable manifest
// through workflow/start and recover through operations/*.
public sealed record ProfileReplayPrepareRequest(
    string RequestId,
    string ResultsDirectory,
    string BaselineDirectory,
    string CandidateDirectory,
    string NodeId,
    string? CaseName = null,
    int HarnessIndex = 0,
    string Backend = "auto",
    double TimeoutSeconds = 300);
