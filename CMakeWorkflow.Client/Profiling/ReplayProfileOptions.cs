#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

public sealed record ReplayProfileOptions(
    string BaselineDirectory,
    string CandidateDirectory,
    string NodeId,
    string ResultsDirectory,
    string? CaseName = null,
    int HarnessIndex = 0,
    string Backend = "auto",
    double TimeoutSeconds = 300);
