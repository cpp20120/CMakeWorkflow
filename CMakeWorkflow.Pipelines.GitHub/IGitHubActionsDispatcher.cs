namespace CMakeWorkflow.Pipelines.GitHub;

/// <summary>Run ID returned by GitHub's 200 workflow_dispatch response.</summary>
public sealed record GitHubDispatchReceipt(long RunId);

/// <summary>One network POST; this interface must never be retried by the coordinator.</summary>
public interface IGitHubActionsDispatcher
{
    Task<GitHubDispatchReceipt> DispatchOnceAsync(PipelineGitHubActionsStage stage,
        string correlationId, CancellationToken cancellationToken = default);
}
