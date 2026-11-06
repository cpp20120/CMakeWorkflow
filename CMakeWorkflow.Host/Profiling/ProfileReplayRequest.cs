// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host.Profiling;
#pragma warning restore IDE0130

// These analysis/replay/campaign calls are request-scoped, unlike workflow/start.
// A disconnect cancels their token; never assume durable acceptance or retry.
public sealed record ProfileReplayRequest(
    string BaselineDirectory,
    string CandidateDirectory,
    string NodeId,
    string ResultsDirectory,
    string? CaseName = null,
    int HarnessIndex = 0,
    string Backend = "auto",
    double TimeoutSeconds = 300);
