namespace CMakeWorkflow.Pipelines.GitHub;

/// <summary>An observation of one specific GitHub Actions run attempt, not the latest attempt.</summary>
/// <remarks>ResponseSha256 hashes the exact JSON response body read from the authoritative API.</remarks>
public sealed record GitHubRunAttempt(
    long RunId, int RunAttempt, string Repository, string WorkflowPath,
    string HeadSha, string Status, string? Conclusion, string ResponseSha256,
    string? Event = null, string? DisplayTitle = null);

/// <summary>Read-only provider boundary. Implementations must query the pinned run attempt, never trigger a rerun.</summary>
public interface IGitHubActionsRunReader
{
    Task<GitHubRunAttempt> GetAttemptAsync(PipelineGitHubActionsStage expected,
        CancellationToken cancellationToken = default);
}
