using System.Globalization;
using CMakeWorkflow.Pipelines.Persistence;

namespace CMakeWorkflow.Pipelines.GitHub;

/// <summary>Read-only GitHub Actions attachment and explicit observation over the existing durable ledger.</summary>
/// <remarks>
/// Only provider GET operations are permitted; no dispatch, rerun, cancellation or background polling occurs.
/// The caller serializes access to each ledger connection. Competing writers are rejected by ledger CAS.
/// A network or identity failure leaves the durable stage unchanged; ObserveAsync can be repeated after reconnect.
/// </remarks>
public sealed class GitHubActionsStageWatcher(PipelineLedger ledger, IGitHubActionsRunReader reader)
{
    private readonly PipelineLedger ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    private readonly IGitHubActionsRunReader reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>Verify the existing attempt before recording its immutable watch binding.</summary>
    public async Task<PipelineRunSnapshot> AttachReadyAsync(string operationId, string stageId,
        CancellationToken cancellationToken = default)
    {
        PipelineRunSnapshot current = ledger.Get(operationId);
        PipelineGitHubActionsStage expected = ExpectStage(current, stageId, PipelineStageStates.Pending);
        PipelineDocument manifest = PipelineManifest.ParseJson(current.ManifestJson);
        PipelineStage definition = manifest.Stages.Single(s => s.Id == stageId);
        foreach (string prerequisite in definition.DependsOn)
        {
            if (current.Stages.Single(s => s.Id == prerequisite).Status != PipelineStageStates.Succeeded)
            {
                throw new InvalidOperationException("pipeline_prerequisite_not_succeeded");
            }
        }

        // If an API call fails there is no durable transition. CAS rechecks dependencies afterwards.
        GitHubRunAttempt observed = await reader.GetAttemptAsync(expected, cancellationToken);
        Verify(expected, observed);
        return ledger.WatchExistingGitHub(operationId, stageId, current.Revision);
    }

    /// <summary>Query the *same* pinned attempt, verify identity and commit a terminal result only.</summary>
    public async Task<PipelineRunSnapshot> ObserveAsync(string operationId, string stageId,
        CancellationToken cancellationToken = default)
    {
        PipelineRunSnapshot current = ledger.Get(operationId);
        PipelineStageSnapshot stage = current.Stages.SingleOrDefault(s => s.Id == stageId)
            ?? throw new ArgumentException("Unknown pipeline stage.", nameof(stageId));
        if (stage.Kind != "github.actions")
        {
            throw new InvalidOperationException("not_a_github_actions_stage");
        }

        if (stage.Status is PipelineStageStates.Succeeded or PipelineStageStates.Failed)
        {
            return current; // Previously committed, immutable outcome; never reinterpret on a later rerun.
        }

        PipelineGitHubActionsStage expected = ExpectStage(current, stageId, PipelineStageStates.WaitingExternal);
        if (!string.Equals(stage.ExternalReference, Binding(expected), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Persisted GitHub watch binding differs from the frozen manifest.");
        }

        GitHubRunAttempt observed = await reader.GetAttemptAsync(expected, cancellationToken);
        Verify(expected, observed);
        if (observed.Status != "completed")
        {
            return current;
        }

        bool success = observed.Conclusion == "success";
        // The provider's response-body hash is an audit fingerprint, not a cryptographic attestation by GitHub.
        string evidence = "github-attempt:" + expected.Repository + ":" +
            expected.RunId.ToString(CultureInfo.InvariantCulture) + ":" +
            expected.RunAttempt.ToString(CultureInfo.InvariantCulture) + ":sha256:" +
            observed.ResponseSha256.ToUpperInvariant();
        return ledger.RecordOutcome(operationId, stageId, current.Revision, success, evidence);
    }

    /// <summary>Explicitly poll the pinned attempt until a verified terminal outcome is recorded.</summary>
    /// <remarks>Stopping the caller's token stops observation only; no GitHub run is canceled or resubmitted.</remarks>
    public async Task<PipelineRunSnapshot> WatchUntilTerminalAsync(string operationId, string stageId,
        TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        TimeSpan delay = pollInterval ?? TimeSpan.FromSeconds(30);
        if (delay < TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Minimum polling interval is five seconds.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PipelineRunSnapshot current = await ObserveAsync(operationId, stageId, cancellationToken);
            PipelineStageSnapshot stage = current.Stages.Single(s => s.Id == stageId);
            if (stage.Status is PipelineStageStates.Succeeded or PipelineStageStates.Failed)
            {
                return current;
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    private static PipelineGitHubActionsStage ExpectStage(PipelineRunSnapshot current, string stageId,
        string requiredStatus)
    {
        PipelineStageSnapshot stage = current.Stages.SingleOrDefault(s => s.Id == stageId)
            ?? throw new ArgumentException("Unknown pipeline stage.", nameof(stageId));
        if (stage.Kind != "github.actions" || stage.Status != requiredStatus)
        {
            throw new InvalidOperationException("invalid_github_watch_stage_state");
        }

        PipelineDocument manifest = PipelineManifest.ParseJson(current.ManifestJson);
        return manifest.Stages.Single(s => s.Id == stageId).GitHubActions
            ?? throw new InvalidDataException("Admitted GitHub stage has no pinned options.");
    }

    private static void Verify(PipelineGitHubActionsStage expected, GitHubRunAttempt actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (expected.Mode != "attach" || actual.RunId != expected.RunId ||
            actual.RunAttempt != expected.RunAttempt ||
            !string.Equals(actual.Repository, expected.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.HeadSha, expected.ExpectedHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.WorkflowPath, WorkflowPath(expected.Workflow), StringComparison.Ordinal) ||
            !Hash(actual.ResponseSha256))
        {
            throw new InvalidDataException("GitHub Actions run attempt does not match immutable pipeline identity.");
        }

        if (actual.Status == "completed")
        {
            if (actual.Conclusion is not ("success" or "failure" or "cancelled" or "timed_out" or
                "action_required" or "neutral" or "skipped" or "stale" or "startup_failure"))
            {
                throw new InvalidDataException("GitHub completed attempt has unknown or missing conclusion.");
            }
        }
        else if (actual.Status is not ("queued" or "in_progress" or "pending" or "waiting" or "requested") ||
                 actual.Conclusion is not null)
        {
            throw new InvalidDataException("GitHub returned inconsistent run status/conclusion.");
        }
    }

    internal static string WorkflowPath(string workflow) =>
        workflow.StartsWith(".github/workflows/", StringComparison.Ordinal)
            ? workflow : ".github/workflows/" + workflow;

    private static string Binding(PipelineGitHubActionsStage options) =>
        $"github:{options.Repository}:{options.Workflow}:{options.RunId}:{options.RunAttempt}:{options.ExpectedHeadSha}";

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
